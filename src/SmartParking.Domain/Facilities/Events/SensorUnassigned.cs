using SmartParking.Domain.Common;

namespace SmartParking.Domain.Facilities.Events;

/// <summary>An occupancy sensor was taken off its spot; it stays registered, and F4 rejects its events.</summary>
[DomainEvent("SensorUnassigned", 1)]
public sealed record SensorUnassigned(
    Guid Id,
    Guid FacilityId,
    string HardwareId,
    Guid PreviousSpotId) : IDomainEvent;
