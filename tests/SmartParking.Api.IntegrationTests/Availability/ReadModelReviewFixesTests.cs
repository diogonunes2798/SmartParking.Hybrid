using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SmartParking.Api.IntegrationTests.Ingestion;
using SmartParking.Application.Abstractions;
using SmartParking.Application.Availability;
using SmartParking.Application.Ingestion;
using SmartParking.Domain.Facilities;
using SmartParking.Infrastructure.Cache.Availability;
using StackExchange.Redis;

namespace SmartParking.Api.IntegrationTests.Availability;

/// <summary>
/// Fixes of the Phase 4 reviews (docs/revisoes/fase-4-revisao-cache.md and fase-4-revisao-codigo.md): rebuild lock
/// and barrier, rebuild aligned with the log writer group, tombstones, invalid configuration payloads, sensor
/// timestamps in the future and the latency histogram buckets.
/// </summary>
[Collection(AvailabilityTestGroup.Name)]
public sealed class ReadModelReviewFixesTests(AvailabilityFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly long T0 = new DateTimeOffset(2026, 11, 15, 10, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    /// <summary>Finding 1: while a rebuild holds the lock, projector writes change nothing and a second rebuild fails.</summary>
    [Fact]
    public async Task Rebuild_lock_refuses_projector_writes_and_a_second_rebuild()
    {
        var (facility, spots, _) = await fixture.SeedAsync(1);
        var unlinkBefore = await UnlinkCallsAsync();

        await using (var session = await fixture.ReadModel.BeginRebuildAsync(Ct))
        {
            await Assert.ThrowsAsync<ReadModelRebuildingException>(() =>
                fixture.ReadModel.ApplyAsync([new OccupancyChange(Guid.NewGuid(), spots[0], SpotState.Occupied, T0)], Ct));
            await Assert.ThrowsAsync<ReadModelRebuildingException>(() =>
                fixture.ReadModel.UpsertSpotAsync(new SpotSnapshot(Guid.NewGuid(), facility, VehicleType.Car), DateTimeOffset.UtcNow, Ct));
            await Assert.ThrowsAsync<RebuildInProgressException>(() => fixture.ReadModel.BeginRebuildAsync(Ct));
            Assert.Equal("FREE", (string?)await fixture.Db.HashGetAsync(ReadModelKeys.Spot(spots[0]), "state"));

            // The holder of the lock writes through the barrier; the clear uses UNLINK (cache finding 7).
            await session.ClearAsync(Ct);
            Assert.True(await UnlinkCallsAsync() > unlinkBefore);
            Assert.True(await fixture.Db.KeyExistsAsync(ReadModelKeys.RebuildLock), "the clear must not delete the lock");
        }

        Assert.False(await fixture.Db.KeyExistsAsync(ReadModelKeys.RebuildLock));
        await fixture.RebuildAsync(); // leave a loaded read model for the next tests
    }

    /// <summary>
    /// Finding 1, the scenario that broke the counters: two rebuilds (two Workers starting together) and a projector
    /// writing all along. One rebuild wins, the other fails, the projector retries its refused writes, and the counters
    /// end equal to the spot states.
    /// </summary>
    [Fact]
    public async Task Concurrent_rebuilds_and_projector_leave_counters_equal_to_spot_states()
    {
        var zone = $"zone-{Guid.NewGuid():N}";
        var facility = await fixture.CreateFacilityAsync("Parque concorrência", 40.2033, -8.4103, zone);
        var spots = new List<Guid>();
        for (var i = 0; i < 6; i++)
        {
            spots.Add((await fixture.CreateSpotAsync(facility.Id)).Id);
        }

        await fixture.RebuildAsync();

        using var stop = new CancellationTokenSource();
        var refused = 0;
        var projector = Task.Run(
            async () =>
            {
                var ts = T0;
                var state = SpotState.Occupied;
                while (!stop.IsCancellationRequested)
                {
                    ts += 1_000;
                    state = state == SpotState.Free ? SpotState.Occupied : SpotState.Free;
                    var batch = spots.Select(s => new OccupancyChange(Guid.NewGuid(), s, state, ts)).ToList();
                    try
                    {
                        await fixture.ReadModel.ApplyAsync(batch, CancellationToken.None);
                    }
                    catch (ReadModelRebuildingException)
                    {
                        refused++; // the bus would redeliver; here the next batch supersedes it
                    }
                }
            },
            CancellationToken.None);

        var rebuilds = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
        {
            try
            {
                await fixture.RebuildAsync();
                return true;
            }
            catch (RebuildInProgressException)
            {
                return false;
            }
        }));
        await Task.Delay(200, Ct);
        await stop.CancelAsync();
        await projector;

        Assert.Contains(true, rebuilds);
        var free = 0;
        foreach (var spot in spots)
        {
            free += (string?)await fixture.Db.HashGetAsync(ReadModelKeys.Spot(spot), "state") == "FREE" ? 1 : 0;
        }

        var counters = await fixture.FacilityCountersAsync(facility.Id);
        Assert.Equal(spots.Count, counters["spots"]);
        Assert.Equal(free, counters["free"]);
        Assert.Equal(free, counters["free:Car"]);
        Assert.Equal(free, (await fixture.ZoneCountersAsync(zone))["free"]);
        Assert.True(counters["free"] >= 0);
    }

    /// <summary>
    /// Finding 2: an event the availability group has applied and acknowledged, but the log writer has not stored
    /// yet, is missing from MongoDB. The rebuild moves the availability group back to what the log has, so the event
    /// is delivered again and the spot ends in its real state.
    /// </summary>
    [Fact]
    public async Task Rebuild_redelivers_to_the_projector_what_the_log_does_not_have_yet()
    {
        var facility = await fixture.CreateFacilityAsync("Parque atraso do log", 40.2033, -8.4103, $"zone-{Guid.NewGuid():N}");
        var spot = await fixture.CreateSpotAsync(facility.Id);
        await fixture.RebuildAsync();

        // Both groups exist and are caught up.
        await using (var workers = await fixture.StartWorkersAsync())
        {
            await IngestionFixture.EventuallyAsync(CaughtUpAsync, "both groups caught up");
        }

        // An event reaches the stream; the availability group takes and acknowledges it (as if the projector had applied
        // it), while the log writer is down.
        var message = new OccupancyEventMessage(
            Guid.NewGuid(), Guid.NewGuid(), spot.Id, facility.Id, OccupancyEventMessage.Occupied,
            DateTimeOffset.UtcNow, JsonDocument.Parse("{}").RootElement, DateTimeOffset.UtcNow);
        await fixture.Services.GetRequiredService<IEventBus>().PublishAsync(
            OccupancyEventMessage.Topic, [new BusMessage(message.EventId.ToString(), message.ToJson())], Ct);
        var taken = await fixture.Db.StreamReadGroupAsync(
            OccupancyEventMessage.Topic, AvailabilityConsumers.Occupancy, "manual", StreamPosition.NewMessages);
        Assert.Single(taken);
        await fixture.Db.StreamAcknowledgeAsync(OccupancyEventMessage.Topic, AvailabilityConsumers.Occupancy, taken[0].Id);

        await fixture.RebuildAsync(); // MongoDB does not have the event: the spot is rebuilt FREE
        Assert.Equal("FREE", (string?)await fixture.Db.HashGetAsync(ReadModelKeys.Spot(spot.Id), "state"));

        await using (var workers = await fixture.StartWorkersAsync())
        {
            await IngestionFixture.EventuallyAsync(
                async () => (string?)await fixture.Db.HashGetAsync(ReadModelKeys.Spot(spot.Id), "state") == "OCCUPIED",
                "event redelivered to the availability group after the rebuild");
        }
    }

    /// <summary>Finding 5: a configuration event older than a delete, handled later, does not resurrect the spot.</summary>
    [Fact]
    public async Task Late_spot_change_after_its_delete_is_stale_and_does_not_resurrect_it()
    {
        var (facility, spots, zone) = await fixture.SeedAsync(2);
        var created = DateTimeOffset.UtcNow.AddSeconds(5);
        var deleted = created.AddSeconds(1);

        Assert.Equal(ConfigurationOutcome.Applied, await fixture.ReadModel.DeleteSpotAsync(spots[0], deleted, Ct));
        Assert.Equal(
            ConfigurationOutcome.Stale,
            await fixture.ReadModel.UpsertSpotAsync(new SpotSnapshot(spots[0], facility, VehicleType.Electric), created, Ct));

        var counters = await fixture.FacilityCountersAsync(facility);
        Assert.Equal(1, counters["spots"]);
        Assert.Equal(1, counters["free"]);
        Assert.Equal(1, (await fixture.ZoneCountersAsync(zone))["spots"]);
        Assert.Equal("1", (string?)await fixture.Db.HashGetAsync(ReadModelKeys.Spot(spots[0]), "deleted"));
        Assert.NotNull(await fixture.Db.KeyTimeToLiveAsync(ReadModelKeys.Spot(spots[0])));
        Assert.Equal(
            [ApplyOutcome.UnknownSpot],
            await fixture.ReadModel.ApplyAsync([new OccupancyChange(Guid.NewGuid(), spots[0], SpotState.Occupied, T0)], Ct));
    }

    [Fact]
    public async Task Late_facility_change_after_its_delete_is_stale_and_its_late_spots_are_ignored()
    {
        var (facility, spots, zone) = await fixture.SeedAsync(1);
        var at = DateTimeOffset.UtcNow.AddSeconds(5);
        await fixture.ReadModel.DeleteSpotAsync(spots[0], at, Ct);
        Assert.Equal(ConfigurationOutcome.Applied, await fixture.ReadModel.DeleteFacilityAsync(facility, at.AddSeconds(1), Ct));

        var snapshot = new FacilitySnapshot(facility, "Seeded", 40.2033, -8.4103, 100, FacilityType.Covered, zone);
        Assert.Equal(ConfigurationOutcome.Stale, await fixture.ReadModel.UpsertFacilityAsync(snapshot, at, Ct));
        Assert.Equal(
            ConfigurationOutcome.Stale,
            await fixture.ReadModel.UpsertSpotAsync(new SpotSnapshot(Guid.NewGuid(), facility, VehicleType.Car), at, Ct));
        Assert.Null(await fixture.Db.GeoPositionAsync(ReadModelKeys.Geo, facility.ToString()));
        Assert.Equal(ConfigurationOutcome.Unknown, await fixture.ReadModel.DeleteFacilityAsync(facility, at.AddSeconds(2), Ct));
        Assert.Equal(0, (await fixture.ZoneCountersAsync(zone)).GetValueOrDefault("spots"));
    }

    /// <summary>Finding 7: a payload that deserialises but breaks a domain rule is poison and writes nothing.</summary>
    [Fact]
    public async Task Invalid_facility_event_is_poison_and_writes_nothing()
    {
        var facility = Guid.NewGuid();
        var payload = JsonSerializer.SerializeToElement(
            new FacilityChanged(facility, "Fora do GEO", 89, -8.41, 10, FacilityType.Covered, "zona", DateTimeOffset.UtcNow),
            ConfigurationEventMessage.SerializerOptions);
        var message = new ConfigurationEventMessage(Guid.NewGuid(), nameof(FacilityChanged), facility, DateTimeOffset.UtcNow, payload);
        var projector = new ConfigurationProjector(
            fixture.ReadModel, ReadModelGate.Opened(), new ReadModelMetrics(fixture.Services.GetRequiredService<IMeterFactory>()), TimeProvider.System);

        var outcome = await projector.HandleAsync([new DeliveredMessage("1-1", new BusMessage("k", message.ToJson()))], Ct);

        Assert.Equal(["1-1"], outcome.Poison);
        Assert.False(await fixture.Db.KeyExistsAsync(ReadModelKeys.Facility(facility)));
        Assert.Null(await fixture.Db.GeoPositionAsync(ReadModelKeys.Geo, facility.ToString()));
    }

    /// <summary>Cache finding 3: a sensor timestamp far in the future would freeze the spot; RF-01 rejects it.</summary>
    [Fact]
    public async Task Sensor_timestamp_in_the_future_is_rejected()
    {
        var body = IngestionFixture.Event(timestamp: DateTimeOffset.UtcNow.AddHours(1).ToString("O"));

        var response = await fixture.Client.PostAsJsonAsync("/api/ingest/occupancy", new[] { body }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("in the future", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        var nearFuture = IngestionFixture.Event(timestamp: DateTimeOffset.UtcNow.AddMinutes(1).ToString("O"));
        var accepted = await fixture.Client.PostAsJsonAsync("/api/ingest/occupancy", new[] { nearFuture }, Ct);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode); // within the clock skew tolerance
    }

    /// <summary>Code finding 1: the D7 lag histograms have sub-second buckets, so a p95 below 1 s can be computed.</summary>
    [Fact]
    public async Task Pipeline_lag_histograms_have_sub_second_buckets()
    {
        var meters = fixture.Services.GetRequiredService<IMeterFactory>();
        new ReadModelMetrics(meters).Lag(TimeSpan.FromMilliseconds(280), TimeSpan.FromMilliseconds(180));
        new OccupancyIngestionMetrics(meters).LogLag(TimeSpan.FromMilliseconds(80));

        var metrics = await fixture.Client.GetStringAsync("/metrics", Ct);

        foreach (var (name, bucket) in new[]
        {
            ("smartparking_ingestion_e2e_latency_seconds_bucket", "le=\"0.3\""),
            ("smartparking_read_model_bus_lag_seconds_bucket", "le=\"0.2\""),
            ("smartparking_occupancy_log_lag_seconds_bucket", "le=\"0.1\""),
        })
        {
            var line = metrics.Split('\n').FirstOrDefault(l => l.StartsWith(name, StringComparison.Ordinal) && l.Contains(bucket, StringComparison.Ordinal));
            Assert.True(line is not null, $"{name} has no {bucket} bucket");
            Assert.False(line!.EndsWith(" 0", StringComparison.Ordinal), $"the sample did not land in {name}{{{bucket}}}: {line}");
        }
    }

    private async Task<bool> CaughtUpAsync()
    {
        var groups = await fixture.Db.StreamGroupInfoAsync(OccupancyEventMessage.Topic);
        return groups.Length >= 2 && groups.All(g => g.PendingMessageCount == 0 && g.Lag == 0);
    }

    private async Task<long> UnlinkCallsAsync()
    {
        var server = fixture.Redis.GetServers()[0];
        var info = await server.InfoAsync("commandstats");
        var line = info.SelectMany(g => g).FirstOrDefault(kv => kv.Key == "cmdstat_unlink").Value;
        if (line is null)
        {
            return 0;
        }

        var calls = line.Split(',')[0].Split('=')[1];
        return long.Parse(calls, System.Globalization.CultureInfo.InvariantCulture);
    }
}
