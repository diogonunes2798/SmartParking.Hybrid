using SmartParking.Domain.Common;

namespace SmartParking.Domain.Facilities.Events;

/// <summary>A parking facility changed; the payload carries the new state.</summary>
[DomainEvent("FacilityUpdated", 1)]
public sealed record FacilityUpdated(
    Guid Id,
    Guid OperatorId,
    string Name,
    double Latitude,
    double Longitude,
    string TimeZone,
    FacilityStatus Status) : IDomainEvent;
