using SmartParking.Domain.Common;

namespace SmartParking.Domain.Facilities.Events;

/// <summary>A zone was deleted; the payload carries its last state.</summary>
[DomainEvent("ZoneDeleted", 1)]
public sealed record ZoneDeleted(
    Guid Id,
    Guid FacilityId,
    string Name) : IDomainEvent;
