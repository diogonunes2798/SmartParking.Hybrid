using SmartParking.Domain.Common;

namespace SmartParking.Domain.Occupancy;

public enum OccupancyEventType
{
    Occupied = 1,
    Freed = 2,
}

/// <summary>
/// Immutable occupancy event emitted by a sensor (spec §3, UC-01). Stored append-only in MongoDB (D3);
/// it has no relational mapping. <see cref="Payload"/> holds the extra fields of each sensor generation
/// (e.g. temperature, airQuality, noise in UC-05) so the schema can evolve without migrations (RNF-04).
/// Ingestion, persistence and idempotency arrive in Phase 3.
/// </summary>
public sealed record OccupancyEvent
{
    public OccupancyEvent(
        Guid eventId,
        Guid sensorId,
        Guid spotId,
        Guid facilityId,
        OccupancyEventType type,
        DateTimeOffset timestamp,
        IReadOnlyDictionary<string, object?>? payload = null)
    {
        EventId = Guard.Id(eventId, nameof(eventId));
        SensorId = Guard.Id(sensorId, nameof(sensorId));
        SpotId = Guard.Id(spotId, nameof(spotId));
        FacilityId = Guard.Id(facilityId, nameof(facilityId));
        Type = Guard.Defined(type, nameof(type));
        Timestamp = Guard.Utc(timestamp);
        Payload = payload ?? new Dictionary<string, object?>();
    }

    /// <summary>Idempotency key (UC-01, step 1).</summary>
    public Guid EventId { get; }

    public Guid SensorId { get; }

    public Guid SpotId { get; }

    public Guid FacilityId { get; }

    public OccupancyEventType Type { get; }

    /// <summary>Sensor clock; the start of the end-to-end latency measurement (D7).</summary>
    public DateTimeOffset Timestamp { get; }

    public IReadOnlyDictionary<string, object?> Payload { get; }
}
