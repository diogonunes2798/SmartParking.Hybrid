using SmartParking.Domain.Occupancy;

namespace SmartParking.Application.Abstractions;

/// <summary>
/// One occupancy event as stored in the immutable log (D3). <see cref="PayloadJson"/> is the extensible
/// <c>payload</c> exactly as the sensor sent it (UC-05, RNF-04): the log stores it without a schema.
/// <see cref="ReceivedAt"/> is when the API accepted it (start of the bus segment of the D7 latency).
/// </summary>
public sealed record OccupancyEventRecord(
    Guid EventId,
    Guid SensorId,
    Guid SpotId,
    Guid FacilityId,
    OccupancyEventType Type,
    DateTimeOffset Timestamp,
    string PayloadJson,
    DateTimeOffset ReceivedAt);

/// <summary>
/// Outcome of an append: how many records were new, how many were already in the log, and which ones the store
/// refused for a reason that will not change on retry (e.g. a field name the engine does not accept in
/// <c>payload</c>). Transient failures are thrown instead, so that the caller retries the whole batch.
/// </summary>
public sealed record AppendResult(int Inserted, int Duplicates, IReadOnlyList<Guid> Refused)
{
    public static AppendResult Empty { get; } = new(0, 0, []);
}

/// <summary>
/// Append-only log of occupancy events (D3, «EM ABERTO»; default: MongoDB, ADR-0007). There is no update and no
/// delete: the log is never used to rebuild aggregates. Appending is idempotent by <see cref="OccupancyEventRecord.EventId"/>
/// (the store enforces uniqueness), which is the final guarantee against the bus delivering a message twice.
/// </summary>
public interface IOccupancyEventLog
{
    Task<AppendResult> AppendAsync(IReadOnlyList<OccupancyEventRecord> records, CancellationToken cancellationToken);
}
