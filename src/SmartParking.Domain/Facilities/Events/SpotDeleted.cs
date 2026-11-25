using SmartParking.Domain.Common;

namespace SmartParking.Domain.Facilities.Events;

/// <summary>A parking spot was deleted; the payload carries its last state.</summary>
[DomainEvent("SpotDeleted", 1)]
public sealed record SpotDeleted(
    Guid Id,
    Guid FacilityId,
    Guid ZoneId,
    string Code,
    SpotType Type,
    SpotStatus Status) : IDomainEvent;
