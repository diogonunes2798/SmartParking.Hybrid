using SmartParking.Domain.Common;

namespace SmartParking.Domain.Facilities.Events;

/// <summary>A sensor was removed from the registry; the payload carries its last state.</summary>
[DomainEvent("SensorRemoved", 1)]
public sealed record SensorRemoved(
    Guid Id,
    Guid FacilityId,
    string HardwareId,
    SensorKind Kind,
    string Model,
    SensorStatus Status,
    Guid? ZoneId) : IDomainEvent;
