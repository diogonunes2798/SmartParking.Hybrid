using SmartParking.Domain.Common;

namespace SmartParking.Domain.Facilities.Events;

/// <summary>A zone changed; the payload carries the new state.</summary>
[DomainEvent("ZoneUpdated", 1)]
public sealed record ZoneUpdated(
    Guid Id,
    Guid FacilityId,
    string Name) : IDomainEvent;
