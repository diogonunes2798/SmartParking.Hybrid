using SmartParking.Domain.Common;

namespace SmartParking.Domain.Facilities.Events;

/// <summary>An occupancy sensor was placed on a spot of its facility. <c>PreviousSpotId</c> is null when it had none.</summary>
[DomainEvent("SensorReassigned", 1)]
public sealed record SensorReassigned(
    Guid Id,
    Guid FacilityId,
    string HardwareId,
    Guid SpotId,
    Guid? PreviousSpotId) : IDomainEvent;
