using SmartParking.Domain.Common;

namespace SmartParking.Domain.Facilities.Events;

/// <summary>A parking facility was registered. F5 uses it for the GEO index.</summary>
[DomainEvent("FacilityCreated", 1)]
public sealed record FacilityCreated(
    Guid Id,
    Guid OperatorId,
    string Name,
    double Latitude,
    double Longitude,
    string TimeZone,
    FacilityStatus Status) : IDomainEvent;
