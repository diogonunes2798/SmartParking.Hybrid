using SmartParking.Application.Abstractions;
using SmartParking.Domain.Occupancy;

namespace SmartParking.Application.Ingestion;

/// <summary>
/// UC-01 step 3: consumer that appends occupancy events to the immutable log (D3). It returns only after the log
/// has the whole batch, so the bus acknowledges nothing that is not stored. It is idempotent: a redelivered event
/// is a duplicate key in the log and counts as done, never as a second document.
/// Malformed messages, and events the log refuses permanently, are reported as poison (they would fail on every
/// redelivery and block the consumer group).
/// </summary>
public sealed class OccupancyLogWriter(
    IOccupancyEventLog log,
    OccupancyIngestionMetrics metrics,
    TimeProvider time) : IMessageBatchHandler
{
    public async Task<BatchOutcome> HandleAsync(IReadOnlyList<DeliveredMessage> messages, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var records = new List<OccupancyEventRecord>(messages.Count);
        var messageIds = new Dictionary<Guid, List<string>>();
        var poison = new List<string>();
        var repeatsInRead = 0;

        foreach (var delivered in messages)
        {
            if (OccupancyEventMessage.TryParse(delivered.Message.Body) is { } m)
            {
                // The same event may appear twice in one read (repeated request): both entries map to one record,
                // so the log sees each eventId once per batch and the extra copy counts as a duplicate.
                if (messageIds.TryGetValue(m.EventId, out var ids))
                {
                    ids.Add(delivered.Id);
                    repeatsInRead++;
                    continue;
                }

                messageIds[m.EventId] = [delivered.Id];
                records.Add(new OccupancyEventRecord(
                    m.EventId,
                    m.SensorId,
                    m.SpotId,
                    m.FacilityId,
                    m.Type == OccupancyEventMessage.Occupied ? OccupancyEventType.Occupied : OccupancyEventType.Freed,
                    m.Timestamp,
                    m.Payload.GetRawText(),
                    m.ReceivedAt));
            }
            else
            {
                poison.Add(delivered.Id);
            }
        }

        var result = records.Count > 0
            ? await log.AppendAsync(records, cancellationToken).ConfigureAwait(false)
            : AppendResult.Empty;

        // One record per eventId, so each refused event maps once to all its entries (review of Phase 3, finding 9).
        poison.AddRange(result.Refused.SelectMany(id => messageIds[id]));

        // Lag only for the events written now: a redelivered duplicate after a recovery would add minutes to the
        // histogram for a write that happened earlier and distort the D7 measurement (finding 9).
        var notWritten = result.DuplicateIds.Concat(result.Refused).ToHashSet();
        var now = time.GetUtcNow();
        foreach (var record in records.Where(r => !notWritten.Contains(r.EventId)))
        {
            metrics.LogLag(now - record.ReceivedAt);
        }

        metrics.Logged(result.Inserted, result.Duplicates + repeatsInRead, poison.Count);
        return poison.Count == 0 ? BatchOutcome.AllProcessed : new BatchOutcome(poison);
    }
}
