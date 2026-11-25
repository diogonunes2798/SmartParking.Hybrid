using SmartParking.Domain.Common;

namespace SmartParking.Domain.Facilities.Events;

/// <summary>A sensor device was registered. F4 validates incoming events against it.</summary>
[DomainEvent("SensorRegistered", 1)]
public sealed record SensorRegistered(
    Guid Id,
    Guid FacilityId,
    string HardwareId,
    SensorKind Kind,
    string Model,
    DateTimeOffset InstalledAt,
    SensorStatus Status,
    Guid? SpotId,
    Guid? ZoneId) : IDomainEvent;
