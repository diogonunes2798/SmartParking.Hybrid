using SmartParking.Domain.Common;

namespace SmartParking.Domain.Facilities.Events;

/// <summary>A sensor's model, status or zone changed; the payload carries the new state.</summary>
[DomainEvent("SensorUpdated", 1)]
public sealed record SensorUpdated(
    Guid Id,
    Guid FacilityId,
    string HardwareId,
    SensorKind Kind,
    string Model,
    DateTimeOffset InstalledAt,
    SensorStatus Status,
    Guid? SpotId,
    Guid? ZoneId) : IDomainEvent;
