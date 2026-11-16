using System.Diagnostics;
using System.Text.Json;
using SmartParking.Application.Abstractions;
using SmartParking.Application.Availability;
using SmartParking.Application.Diagnostics;
using SmartParking.Domain.Facilities;

namespace SmartParking.Application.Outbox;

/// <summary>
/// Topic of each outbox event type (Phase 5, ADR-0009). Configuration events (RF-06) go to the stream the read model
/// already consumes (ADR-0008). Every other type (sessions and payments, UC-02) goes to <see cref="DomainEvents"/>,
/// which the summary tables of Phase 6 (UC-04, D4) will consume; until then the stream only accumulates.
/// </summary>
public static class OutboxTopics
{
    /// <summary>Session and payment events (and any future type without its own topic).</summary>
    public const string DomainEvents = "smartparking:domain-events";

    private static readonly HashSet<string> ConfigurationTypes =
        [nameof(FacilityChanged), nameof(FacilityDeleted), nameof(SpotChanged), nameof(SpotDeleted)];

    public static string For(string type) =>
        ConfigurationTypes.Contains(type) ? ConfigurationEventMessage.Topic : DomainEvents;
}

/// <summary>Settings of the outbox relay (section <c>Outbox</c>).</summary>
public sealed class OutboxRelayOptions
{
    public const string SectionName = "Outbox";

    /// <summary>False disables the relay in this process (e.g. to run a second Workers host without it).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Rows claimed per batch (<c>LIMIT</c> of the <c>FOR UPDATE SKIP LOCKED</c> query).</summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>Wait after an empty or partial batch. Same value as the stream consumers (ADR-0006).</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Wait after a failed batch (database or bus down).</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>How often the backlog gauge (<c>smartparking.outbox.pending</c>) is refreshed.</summary>
    public TimeSpan PendingRefreshInterval { get; set; } = TimeSpan.FromSeconds(5);

    public void Validate()
    {
        if (BatchSize is < 1 or > 10_000)
        {
            throw new InvalidOperationException($"{SectionName}:BatchSize must be between 1 and 10000.");
        }

        if (PollInterval <= TimeSpan.Zero || RetryDelay <= TimeSpan.Zero || PendingRefreshInterval <= TimeSpan.Zero)
        {
            throw new InvalidOperationException($"{SectionName} intervals must be positive.");
        }
    }
}

/// <summary>
/// Publishes the transactional outbox (UC-02, RF-06; spec §4 and §5; ADR-0004, ADR-0009). One call relays one batch:
/// claim (locked, <c>SKIP LOCKED</c>) → publish on the bus (D2) → mark processed and commit. If the process stops
/// between the publish and the commit, the rows are published again by the next instance: delivery is at least once
/// and the consumers are idempotent (spec §5). Order: oldest <c>occurred_at</c> first inside a batch; across concurrent
/// instances it is not guaranteed, which the consumers tolerate because every event carries the full state and a
/// late older event is ignored (ADR-0008).
/// </summary>
public sealed class OutboxRelay(IOutboxStore store, IEventBus bus, OutboxMetrics metrics, TimeProvider time)
{
    /// <summary>Relays at most <paramref name="maxCount"/> rows; returns how many were published.</summary>
    public async Task<int> RelayBatchAsync(int maxCount, CancellationToken cancellationToken)
    {
        var batch = await store.ClaimAsync(maxCount, cancellationToken).ConfigureAwait(false);
        await using (batch.ConfigureAwait(false))
        {
            var entries = batch.Entries;
            if (entries.Count == 0)
            {
                return 0;
            }

            var spans = new List<Activity>(entries.Count);
            try
            {
                // Per topic, in outbox order. One XADD pipeline per topic (RedisStreamsEventBus).
                foreach (var topic in entries.GroupBy(e => OutboxTopics.For(e.Type)))
                {
                    var messages = topic.Select(e => ToBusMessage(topic.Key, e, spans)).ToList();
                    await bus.PublishAsync(topic.Key, messages, cancellationToken).ConfigureAwait(false);
                }

                var publishedAt = time.GetUtcNow();

                // Published: commit even if shutdown was requested meanwhile, otherwise every row would be published
                // again on restart. Bounded by the command timeout of the connection.
                await batch.CompleteAsync(publishedAt, CancellationToken.None).ConfigureAwait(false);
                metrics.Published(entries, publishedAt);
                return entries.Count;
            }
            catch (Exception ex)
            {
                foreach (var span in spans)
                {
                    span.SetStatus(ActivityStatusCode.Error, ex.GetType().Name);
                }

                throw;
            }
            finally
            {
                foreach (var span in spans)
                {
                    span.Dispose();
                }
            }
        }
    }

    /// <summary>
    /// One producer span per row, child of the request that wrote it (trace propagation, ADR-0009). The message
    /// carries this span's context, so the consumer continues the same trace. Without a listener (no tracing), the
    /// stored context is passed through unchanged.
    /// </summary>
    /// <remarks>
    /// <see cref="Activity.Current"/> is cleared around each start (review of Phase 5, finding 2): otherwise a row without
    /// a stored context would become a child of the previous row's span (open until the batch ends), chaining unrelated
    /// events into one trace, and the bus and commit spans would join the last row's trace.
    /// </remarks>
    private static BusMessage ToBusMessage(string topic, OutboxEntry entry, List<Activity> spans)
    {
        ActivityContext.TryParse(entry.TraceParent, null, out var parent);
        var previous = Activity.Current;
        Activity.Current = null;
        Activity? span;
        try
        {
            span = SmartParkingTelemetry.ActivitySource.StartActivity("outbox publish", ActivityKind.Producer, parent);
        }
        finally
        {
            Activity.Current = previous;
        }

        string? traceParent = entry.TraceParent;
        if (span is not null)
        {
            span.SetTag("messaging.system", "redis");
            span.SetTag("messaging.destination.name", topic);
            span.SetTag("messaging.message.id", entry.Id.ToString());
            span.SetTag("smartparking.outbox.type", entry.Type);
            spans.Add(span);
            traceParent = span.IdFormat == ActivityIdFormat.W3C ? span.Id : traceParent;
        }

        return new BusMessage(entry.Id.ToString(), Body(entry), traceParent);
    }

    /// <summary>
    /// The <see cref="ConfigurationEventMessage"/> JSON, with the payload copied as is (review of Phase 5, finding 1): the
    /// column is <c>jsonb</c>, so it is always valid JSON, and parsing it again could only add a failure that recurs for the
    /// same row (rolling back the oldest batch forever) plus a parse, a clone and a serialisation per row.
    /// </summary>
    private static string Body(OutboxEntry entry)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>(entry.Payload.Length + 160);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("id", entry.Id);
            writer.WriteString("type", entry.Type);
            writer.WriteString("aggregateId", entry.AggregateId);
            writer.WriteString("occurredAt", entry.OccurredAt);
            writer.WritePropertyName("payload");
            writer.WriteRawValue(entry.Payload, skipInputValidation: true);
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
