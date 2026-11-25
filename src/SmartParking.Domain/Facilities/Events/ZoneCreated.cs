using SmartParking.Domain.Common;

namespace SmartParking.Domain.Facilities.Events;

/// <summary>A zone was added to a parking facility.</summary>
[DomainEvent("ZoneCreated", 1)]
public sealed record ZoneCreated(
    Guid Id,
    Guid FacilityId,
    string Name) : IDomainEvent;
