using Microsoft.Extensions.Logging;
using Npgsql;
using SmartParking.Application.Abstractions;
using SmartParking.Application.Availability;
using SmartParking.Application.Common;
using SmartParking.Application.Ingestion;
using SmartParking.Domain.Occupancy;

namespace SmartParking.Infrastructure.Relational.Monolithic;

/// <summary>
/// <see cref="IEventBus"/> of the monolithic variant (ADR-0012, Phase 8 decision «escrita síncrona»). There is no broker
/// and there are no consumers: publishing an occupancy event <em>is</em> writing it. The events and the new state of their
/// spots are committed in one transaction (<see cref="MonolithicOccupancyStore"/>) before the API answers 202, so a
/// following availability query already sees them. This is the classic design of a single relational system.
/// <para>
/// The other topics (configuration and domain events relayed from the outbox) have no subscriber in this variant: the
/// availability query reads the configuration tables directly. The relay still runs and marks the rows processed, so the
/// write cost of UC-02 and RF-06 (the outbox row in the same transaction) is the same in both variants.
/// </para>
/// </summary>
public sealed partial class PostgresEventBus(
    MonolithicOccupancyStore store,
    MonolithicOptions options,
    OccupancyIngestionMetrics ingestionMetrics,
    ReadModelMetrics readModelMetrics,
    TimeProvider time,
    ILogger<PostgresEventBus> logger) : IEventBus
{
    public async Task PublishAsync(string topic, IReadOnlyList<BusMessage> messages, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(messages);
        if (topic != OccupancyEventMessage.Topic || messages.Count == 0)
        {
            return; // no subscriber in the monolithic variant (see the class remarks)
        }

        var records = new List<OccupancyEventRecord>(messages.Count);
        var poison = 0;
        foreach (var message in messages)
        {
            if (OccupancyEventMessage.TryParse(message.Body) is { } m)
            {
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
                poison++; // cannot happen with the API as producer; counted like the log writer does
            }
        }

        var inserted = new List<OccupancyEventRecord>(records.Count);
        var applied = new List<OccupancyEventRecord>(records.Count);
        var result = await WriteAsync(records, inserted, applied, cancellationToken).ConfigureAwait(false);

        // Same instruments and sampling rules as the hybrid pipeline (Cap. 6.5): reception → log write for every new event,
        // as the log writer does; sensor → state visible to UC-03 (D7, «e2e_latency») only for the events that became the
        // state of a registered spot, as the Redis projector does (review of Phase 8, architecture finding M1).
        var now = time.GetUtcNow();
        foreach (var record in inserted)
        {
            ingestionMetrics.LogLag(now - record.ReceivedAt);
        }

        foreach (var record in applied)
        {
            readModelMetrics.Lag(now - record.Timestamp, now - record.ReceivedAt);
        }

        ingestionMetrics.Logged(result.Inserted, result.Duplicates, poison + result.Refused.Count);
    }

    private async Task<AppendResult> WriteAsync(
        List<OccupancyEventRecord> records,
        List<OccupancyEventRecord> inserted,
        List<OccupancyEventRecord> applied,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                inserted.Clear();
                applied.Clear();
                return await store.AppendCoreAsync(records, cancellationToken, inserted, applied).ConfigureAwait(false);
            }
            catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure
                && attempt < options.MaxWriteAttempts)
            {
                LogRetry(logger, ex.SqlState, attempt);
            }
            catch (NpgsqlException ex) when (ex.IsTransient)
            {
                // Connection lost, lock or statement timeout (e.g. behind a migration's ACCESS EXCLUSIVE lock), too many
                // connections: the API answers 503 and the sender retries (UC-01), exactly as when Redis is down.
                throw new EventBusUnavailableException("PostgreSQL could not store the occupancy events.", ex);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Occupancy batch hit PostgreSQL error {SqlState}; retrying (attempt {Attempt}).")]
    private static partial void LogRetry(ILogger logger, string sqlState, int attempt);
}
