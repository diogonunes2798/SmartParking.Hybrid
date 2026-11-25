using SmartParking.Domain.Common;

namespace SmartParking.Domain.Facilities.Events;

/// <summary>A parking spot changed; activation and deactivation are updates of <c>Status</c>. <c>PreviousZoneId</c> lets the counters move the spot between zones (spec 001).</summary>
[DomainEvent("SpotUpdated", 1)]
public sealed record SpotUpdated(
    Guid Id,
    Guid FacilityId,
    Guid ZoneId,
    Guid PreviousZoneId,
    string Code,
    SpotType Type,
    SpotStatus Status) : IDomainEvent;
