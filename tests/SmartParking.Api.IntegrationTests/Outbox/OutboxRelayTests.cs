using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SmartParking.Api.IntegrationTests.Availability;
using SmartParking.Api.IntegrationTests.Ingestion;
using SmartParking.Api.IntegrationTests.Relational;
using SmartParking.Application.Abstractions;
using SmartParking.Application.Availability;
using SmartParking.Application.Common;
using SmartParking.Application.Configuration;
using SmartParking.Application.Diagnostics;
using SmartParking.Application.Outbox;
using SmartParking.Domain.Facilities;
using SmartParking.Domain.Sessions;
using SmartParking.Infrastructure.Cache.Availability;
using StackExchange.Redis;

namespace SmartParking.Api.IntegrationTests.Outbox;

/// <summary>
/// Phase 5 (UC-02, RF-06, ADR-0009): the outbox relay publishes every row at least once and never twice in normal
/// operation, the read model applies each event once, the trace continues from the HTTP request to the consumer, and
/// the consistency window is measured. Runs in the read model collection (one at a time): the relay drains the whole
/// outbox of that database, and the tests count the events the projector applies.
/// </summary>
[Collection(AvailabilityTestGroup.Name)]
public sealed class OutboxRelayTests(AvailabilityFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Required test 1. The relay stops in the middle of a batch: after publishing <paramref name="publishedBeforeCrash"/>
    /// of its 7 rows (or all of them, but before the commit). On restart nothing is lost (every row ends processed and
    /// the read model is complete) and nothing is applied twice (7 «applied», the republished rows are «stale»).
    /// </summary>
    [Theory]
    [InlineData(3)]
    [InlineData(int.MaxValue)] // everything published, the commit never happened
    public async Task Relay_stopped_mid_batch_loses_nothing_and_the_read_model_applies_each_event_once(int publishedBeforeCrash)
    {
        await DrainAsync(); // rows left by other tests of the collection must not enter the counts below
        await using var workers = await fixture.StartWorkersAsync();
        await ConfigurationGroupCaughtUpAsync(); // deliveries of earlier tests must not be counted either
        using var outcomes = new ProjectorListener(workers.Services.GetRequiredService<IMeterFactory>());

        var zone = $"zone-{Guid.NewGuid():N}";
        var facility = await fixture.CreateFacilityAsync("Parque da Baixa", 40.2105, -8.4300, zone);
        var spots = new List<Guid>();
        for (var i = 0; i < 6; i++)
        {
            spots.Add((await fixture.CreateSpotAsync(facility.Id)).Id);
        }

        var ids = await PendingIdsAsync();
        Assert.Equal(7, ids.Count);

        var crashing = new CrashingBus(fixture.Services.GetRequiredService<IEventBus>(), publishedBeforeCrash);
        await Assert.ThrowsAsync<EventBusUnavailableException>(() => Relay(crashing).RelayBatchAsync(100, Ct));
        Assert.Equal(7, (await PendingIdsAsync()).Count); // rolled back: still to publish

        // Restart: a new relay with a healthy bus.
        Assert.Equal(7, await Relay().RelayBatchAsync(100, Ct));
        Assert.Empty(await PendingIdsAsync());

        var republished = Math.Min(publishedBeforeCrash, 7);
        await IngestionFixture.EventuallyAsync(
            () => Task.FromResult(outcomes.Count("applied") + outcomes.Count("stale") == 7 + republished),
            "projector handled every delivery");
        await Task.Delay(300, Ct); // anything else in flight would show up now

        Assert.Equal(7, outcomes.Count("applied"));
        Assert.Equal(republished, outcomes.Count("stale"));
        Assert.Equal(7, outcomes.WindowSamples); // one consistency window per applied event, none for redeliveries
        var counters = await fixture.FacilityCountersAsync(facility.Id);
        Assert.Equal(6, counters["spots"]);
        Assert.Equal(6, counters["free"]);
        Assert.Equal(6, (await fixture.ZoneCountersAsync(zone))["spots"]);
    }

    /// <summary>
    /// Required test 2. Two relay instances drain 500 rows at the same time: every row is published exactly once, and both
    /// instances took part. The first publish waits until the other instance is publishing too, while it still holds its
    /// batch (review of Phase 5, finding 4): with a plain <c>FOR UPDATE</c> (no <c>SKIP LOCKED</c>) the second instance
    /// would wait for those locks instead, and the first one times out.
    /// </summary>
    [Fact]
    public async Task Two_relays_in_parallel_never_publish_the_same_row_twice()
    {
        var ids = await InsertSessionRowsAsync(500);
        var bus = new BarrierBus(fixture.Services.GetRequiredService<IEventBus>());

        async Task<int> RunAsync(OutboxRelay relay)
        {
            var total = 0;
            int published;
            while ((published = await relay.RelayBatchAsync(20, Ct)) > 0)
            {
                total += published;
            }

            return total;
        }

        var results = await Task.WhenAll(Task.Run(() => RunAsync(Relay(bus)), Ct), Task.Run(() => RunAsync(Relay(bus)), Ct));

        Assert.True(bus.Overlapped, "the two instances never published at the same time");
        Assert.All(results, r => Assert.True(r > 0, $"an instance published nothing: {string.Join(", ", results)}"));
        Assert.Empty(await PendingIdsAsync());

        var wanted = ids.Select(i => i.ToString()).ToHashSet();
        var entries = await fixture.Db.StreamRangeAsync(OutboxTopics.DomainEvents);
        var keys = entries.Select(e => e["key"].ToString()).Where(wanted.Contains).ToList();
        Assert.Equal(500, keys.Count);
        Assert.Equal(500, keys.Distinct().Count());
    }

    /// <summary>The trace of the HTTP request continues in the relay and in the projector (traceparent, ADR-0009).</summary>
    [Fact]
    public async Task Trace_continues_from_the_http_request_to_the_consumer()
    {
        await DrainAsync();
        var spans = new ConcurrentBag<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == SmartParkingTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = spans.Add,
        };
        ActivitySource.AddActivityListener(listener);
        await using var workers = await fixture.StartWorkersAsync();

        var traceId = ActivityTraceId.CreateRandom();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/facilities")
        {
            Content = JsonContent.Create(
                new CreateFacility("Parque Traçado", new GeoJsonPoint("Point", [-8.42, 40.20]), 50, FacilityType.Covered, "CMC", $"zone-{Guid.NewGuid():N}"),
                options: RelationalFixture.Json),
        };
        request.Headers.Add("traceparent", $"00-{traceId}-{ActivitySpanId.CreateRandom()}-01");
        var response = await fixture.Client.SendAsync(request, Ct);
        response.EnsureSuccessStatusCode();
        var facility = (await response.Content.ReadFromJsonAsync<FacilityDto>(RelationalFixture.Json, Ct))!;

        var row = Assert.Single(await fixture.OutboxAsync(facility.Id));
        Assert.Contains(traceId.ToString(), row.TraceParent, StringComparison.Ordinal);

        Assert.True(await Relay().RelayBatchAsync(100, Ct) >= 1);
        await IngestionFixture.EventuallyAsync(
            () => Task.FromResult(spans.Any(s => s.OperationName == "apply configuration event" && s.TraceId == traceId)),
            "consumer span in the request's trace");

        var publish = Assert.Single(spans, s => s.OperationName == "outbox publish" && s.TraceId == traceId);
        var apply = Assert.Single(spans, s => s.OperationName == "apply configuration event" && s.TraceId == traceId);
        Assert.Equal(publish.SpanId, apply.ParentSpanId);
        Assert.Equal(ActivityKind.Producer, publish.Kind);

        var entry = (await fixture.Db.StreamRangeAsync(ConfigurationEventMessage.Topic))
            .Single(e => e["key"] == row.Id.ToString());
        Assert.Contains(traceId.ToString(), entry["traceparent"].ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Review of Phase 5, finding 2: rows written outside a traced operation start one root span each, instead of becoming
    /// children of the previous row's span (one chain, unrelated events in one trace).
    /// </summary>
    [Fact]
    public async Task Rows_without_trace_context_start_independent_traces()
    {
        await DrainAsync();
        var spans = new ConcurrentBag<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == SmartParkingTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = spans.Add,
        };
        ActivitySource.AddActivityListener(listener);
        var ids = (await InsertSessionRowsAsync(3)).Select(i => i.ToString()).ToHashSet();

        Assert.Equal(3, await Relay().RelayBatchAsync(100, Ct));

        var mine = spans.Where(s => s.OperationName == "outbox publish"
            && ids.Contains(s.GetTagItem("messaging.message.id") as string ?? string.Empty)).ToList();
        Assert.Equal(3, mine.Count);
        Assert.All(mine, s => Assert.Equal(default, s.ParentSpanId));
        Assert.Equal(3, mine.Select(s => s.TraceId).Distinct().Count());
    }

    /// <summary>Metric names the Phase 9 scripts read, the sub-second buckets and the D7 target from configuration.</summary>
    [Fact]
    public async Task Consistency_window_publish_lag_and_targets_are_exposed_on_metrics()
    {
        // Buckets are cumulative and the meter is shared with the rest of the collection, so a non-zero bucket proves
        // nothing (review of Phase 5, finding 5): the samples below must move exactly one count into (0.1, 0.15] and
        // (0.075, 0.1], the buckets just below the 140 ms and 90 ms recorded.
        var window = ("smartparking_consistency_window_seconds_bucket", "type=\"FacilityChanged\"", "0.1", "0.15");
        var publishLag = ("smartparking_outbox_publish_lag_seconds_bucket", "type=\"SessionClosed\"", "0.075", "0.1");
        var before = await ScrapeAsync();
        var windowBefore = BucketDelta(before, window);
        var lagBefore = BucketDelta(before, publishLag);

        new ReadModelMetrics(fixture.Services.GetRequiredService<IMeterFactory>())
            .ConsistencyWindow(nameof(FacilityChanged), TimeSpan.FromMilliseconds(140));
        fixture.OutboxMetrics.Published(
            [new OutboxEntry(Guid.NewGuid(), nameof(SessionClosed), Guid.NewGuid(), "{}", DateTimeOffset.UtcNow.AddMilliseconds(-90), null)],
            DateTimeOffset.UtcNow);

        // The Prometheus exporter caches a scrape for a short while: scrape until the new samples show up.
        string[] lines = [];
        await IngestionFixture.EventuallyAsync(
            async () =>
            {
                lines = await ScrapeAsync();
                return BucketDelta(lines, window) != windowBefore && BucketDelta(lines, publishLag) != lagBefore;
            },
            "the two samples in a fresh scrape");
        Assert.Equal(windowBefore + 1, BucketDelta(lines, window));
        Assert.Equal(lagBefore + 1, BucketDelta(lines, publishLag));

        string Target(string objective) => lines.Single(l =>
            l.StartsWith("smartparking_target_seconds{", StringComparison.Ordinal)
            && l.Contains($"objective=\"{objective}\"", StringComparison.Ordinal));
        Assert.EndsWith(" 1", Target("uc01_ingestion_e2e_p95"), StringComparison.Ordinal); // D7
        Assert.EndsWith(" 0.2", Target("uc03_availability_query_p95"), StringComparison.Ordinal);
        Assert.DoesNotContain(lines, l => l.Contains("consistency_window_p95", StringComparison.Ordinal)); // TODO(Diogo)
    }

    [Fact]
    public async Task Session_events_go_to_the_domain_events_topic_and_configuration_events_to_the_read_model_topic()
    {
        Assert.Equal(OutboxTopics.DomainEvents, OutboxTopics.For(nameof(SessionStarted)));
        Assert.Equal(OutboxTopics.DomainEvents, OutboxTopics.For(nameof(PaymentRegistered)));
        Assert.Equal(ConfigurationEventMessage.Topic, OutboxTopics.For(nameof(SpotDeleted)));

        await DrainAsync();
        Assert.Equal(0, await Relay().RelayBatchAsync(100, Ct)); // empty outbox: nothing published, no error
    }

    // ---- helpers ----

    private OutboxRelay Relay(IEventBus? bus = null) => new(
        fixture.Services.GetRequiredService<IOutboxStore>(),
        bus ?? fixture.Services.GetRequiredService<IEventBus>(),
        fixture.OutboxMetrics, // one instance: each one registers another backlog gauge (review of Phase 5, finding 5)
        TimeProvider.System);

    private async Task<string[]> ScrapeAsync() => (await fixture.Client.GetStringAsync("/metrics", Ct)).Split('\n');

    /// <summary>Count in the bucket (<c>lower</c>, <c>upper</c>] of one series (0 when the series does not exist yet).</summary>
    private static long BucketDelta(string[] lines, (string Name, string Series, string Lower, string Upper) h)
    {
        long Cumulative(string le) => lines
            .Where(l => l.StartsWith(h.Name, StringComparison.Ordinal) && l.Contains(h.Series, StringComparison.Ordinal)
                && l.Contains($"le=\"{le}\"", StringComparison.Ordinal))
            .Select(l => long.Parse(l[(l.LastIndexOf(' ') + 1)..], System.Globalization.CultureInfo.InvariantCulture))
            .SingleOrDefault();
        return Cumulative(h.Upper) - Cumulative(h.Lower);
    }

    private async Task DrainAsync()
    {
        while (await Relay().RelayBatchAsync(500, Ct) > 0)
        {
        }
    }

    private Task ConfigurationGroupCaughtUpAsync() => IngestionFixture.EventuallyAsync(
        async () =>
        {
            if (!await fixture.Db.KeyExistsAsync(ConfigurationEventMessage.Topic))
            {
                return true;
            }

            var groups = await fixture.Db.StreamGroupInfoAsync(ConfigurationEventMessage.Topic);
            return groups.Any(g => g.Name == AvailabilityConsumers.Configuration && g.PendingMessageCount == 0 && g.Lag == 0);
        },
        "configuration consumer group caught up");

    private async Task<List<Guid>> PendingIdsAsync()
    {
        var ids = new List<Guid>();
        await using var command = fixture.Services.GetRequiredService<NpgsqlDataSource>()
            .CreateCommand("SELECT id FROM outbox_messages WHERE processed_at IS NULL");
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;
    }

    /// <summary>Rows shaped like UC-02 events, written straight to the outbox (bulk, without 500 HTTP requests).</summary>
    private async Task<List<Guid>> InsertSessionRowsAsync(int count)
    {
        var ids = Enumerable.Range(0, count).Select(_ => Guid.CreateVersion7()).ToList();
        await using var command = fixture.Services.GetRequiredService<NpgsqlDataSource>().CreateCommand("""
            INSERT INTO outbox_messages (id, type, aggregate_id, payload, occurred_at)
            SELECT id, 'SessionClosed', gen_random_uuid(), '{}'::jsonb, now()
            FROM unnest($1) AS id
            """);
        command.Parameters.Add(new NpgsqlParameter<Guid[]> { TypedValue = [.. ids] });
        await command.ExecuteNonQueryAsync(Ct);
        return ids;
    }

    /// <summary>Publishes the first <c>publishBeforeCrash</c> messages, then fails as if the process had stopped.</summary>
    private sealed class CrashingBus(IEventBus inner, int publishBeforeCrash) : IEventBus
    {
        public async Task PublishAsync(string topic, IReadOnlyList<BusMessage> messages, CancellationToken cancellationToken)
        {
            await inner.PublishAsync(topic, [.. messages.Take(publishBeforeCrash)], cancellationToken);
            throw new EventBusUnavailableException("Simulated stop of the relay in the middle of a batch.", new TimeoutException());
        }
    }

    /// <summary>
    /// The first publish waits (its batch still locked) until a second publish starts, at most 5 s; then every publish goes
    /// through. Proves that two instances hold different batches at the same time.
    /// </summary>
    private sealed class BarrierBus(IEventBus inner) : IEventBus
    {
        private readonly TaskCompletionSource second = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int calls;

        public bool Overlapped => second.Task.IsCompletedSuccessfully;

        public async Task PublishAsync(string topic, IReadOnlyList<BusMessage> messages, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                await second.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            }
            else
            {
                second.TrySetResult();
            }

            await inner.PublishAsync(topic, messages, cancellationToken);
        }
    }

    /// <summary>
    /// Counts the configuration outcomes and consistency window samples of the fixture's meter factory, shared by the test
    /// Workers host and by projectors other tests build by hand. Valid because the collection runs one test at a time and
    /// every Workers host is disposed at the end of its test (review of Phase 5, finding 3).
    /// </summary>
    private sealed class ProjectorListener : IDisposable
    {
        private readonly MeterListener listener = new();
        private readonly ConcurrentDictionary<string, int> outcomes = new();
        private int windowSamples;

        public ProjectorListener(IMeterFactory hostMeters)
        {
            listener.InstrumentPublished = (instrument, l) =>
            {
                if (ReferenceEquals(instrument.Meter.Scope, hostMeters)
                    && instrument.Name is "smartparking.read_model.configuration" or ReadModelMetrics.ConsistencyWindowInstrument)
                {
                    l.EnableMeasurementEvents(instrument);
                }
            };
            listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            {
                foreach (var tag in tags)
                {
                    if (tag.Key == "outcome")
                    {
                        outcomes.AddOrUpdate((string)tag.Value!, (int)value, (_, c) => c + (int)value);
                    }
                }
            });
            listener.SetMeasurementEventCallback<double>((_, _, _, _) => Interlocked.Increment(ref windowSamples));
            listener.Start();
        }

        public int WindowSamples => Volatile.Read(ref windowSamples);

        public int Count(string outcome) => outcomes.GetValueOrDefault(outcome);

        public void Dispose() => listener.Dispose();
    }
}
