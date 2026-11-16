using System.Diagnostics.Metrics;
using SmartParking.Application.Abstractions;
using SmartParking.Application.Diagnostics;

namespace SmartParking.Application.Outbox;

/// <summary>
/// Metrics of the outbox relay (meter <c>SmartParking</c>, ADR-0002, ADR-0009):
/// <list type="bullet">
/// <item><c>smartparking.outbox.published</c>: rows published, by <c>type</c>;</item>
/// <item><c>smartparking.outbox.batches</c>: batches relayed, by <c>outcome</c> (<c>published</c>, <c>failed</c>);</item>
/// <item><c>smartparking.outbox.publish_lag</c>: seconds from <c>occurred_at</c> to the publication on the bus, by
/// <c>type</c>. First part of the consistency window, and the only one for event types without a read model consumer
/// yet (sessions and payments until Phase 6);</item>
/// <item><c>smartparking.outbox.pending</c>: unprocessed rows (backlog), refreshed by the relay host.</item>
/// </list>
/// Both timestamps come from <see cref="TimeProvider.System"/>: <c>occurred_at</c> in the API process, the publication
/// in the Workers process. On one machine they read the same clock (ADR-0009).
/// </summary>
public sealed class OutboxMetrics
{
    private readonly Counter<long> published;
    private readonly Counter<long> batches;
    private readonly Histogram<double> publishLag;
    private long pending = -1;

    public OutboxMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        var meter = meterFactory.Create(SmartParkingTelemetry.MeterName);
        published = meter.CreateCounter<long>("smartparking.outbox.published", "{event}", "Outbox rows published on the bus.");
        batches = meter.CreateCounter<long>("smartparking.outbox.batches", "{batch}", "Outbox batches relayed, by outcome.");
        publishLag = meter.CreateHistogram<double>(
            "smartparking.outbox.publish_lag", "s", "Time from the outbox occurred_at to the publication on the bus.");
        meter.CreateObservableGauge(
            "smartparking.outbox.pending",
            () => Interlocked.Read(ref pending) is var value and >= 0 ? [new Measurement<long>(value)] : Array.Empty<Measurement<long>>(),
            "{event}",
            "Unprocessed outbox rows.");
    }

    public void Published(IReadOnlyList<OutboxEntry> entries, DateTimeOffset publishedAt)
    {
        ArgumentNullException.ThrowIfNull(entries);
        batches.Add(1, new KeyValuePair<string, object?>("outcome", "published"));
        foreach (var entry in entries)
        {
            var type = new KeyValuePair<string, object?>("type", entry.Type);
            published.Add(1, type);
            publishLag.Record((publishedAt - entry.OccurredAt).TotalSeconds, type);
        }
    }

    public void Failed() => batches.Add(1, new KeyValuePair<string, object?>("outcome", "failed"));

    public void Pending(long count) => Interlocked.Exchange(ref pending, count);
}
