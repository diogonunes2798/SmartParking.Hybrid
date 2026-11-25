using SmartParking.Domain.Common;

namespace SmartParking.Domain.Facilities.Events;

/// <summary>A parking spot was added. F5 uses it to initialise the free-spot counters of the facility and zone.</summary>
[DomainEvent("SpotCreated", 1)]
public sealed record SpotCreated(
    Guid Id,
    Guid FacilityId,
    Guid ZoneId,
    string Code,
    SpotType Type,
    SpotStatus Status) : IDomainEvent;
