using SmartParking.Domain.Common;

namespace SmartParking.Domain.Facilities.Events;

/// <summary>A parking facility was deleted; the payload carries its last state.</summary>
[DomainEvent("FacilityDeleted", 1)]
public sealed record FacilityDeleted(
    Guid Id,
    Guid OperatorId,
    string Name,
    double Latitude,
    double Longitude,
    string TimeZone,
    FacilityStatus Status) : IDomainEvent;
