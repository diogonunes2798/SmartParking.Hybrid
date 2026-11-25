namespace SmartParking.Domain.Occupancy;

/// <summary>
/// An immutable fact reported by an occupancy sensor (spec 003, D3). It is not an aggregate root: it is appended
/// once to the event log and never changed or deleted. <see cref="FacilityId"/> and <see cref="ZoneId"/> are the
/// spot's context at ingestion time, so the history does not change when the spot later moves to another zone.
/// <see cref="Payload"/> is the device's extra data as raw JSON (an object, or <c>null</c>), kept as received (UC-05).
/// </summary>
public sealed record OccupancyEvent(
    Guid EventId,
    Guid SensorId,
    string HardwareId,
    Guid SpotId,
    Guid FacilityId,
    Guid ZoneId,
    OccupancyEventType Type,
    DateTimeOffset Timestamp,
    DateTimeOffset ReceivedAt,
    string? Payload);
