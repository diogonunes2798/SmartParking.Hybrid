using SmartParking.Domain.Common;

namespace SmartParking.Domain.Sessions.Events;

/// <summary>An active reservation was cancelled. Identifiers and instants only.</summary>
[DomainEvent("ReservationCancelled", 1)]
public sealed record ReservationCancelled(
    Guid Id,
    Guid SpotId,
    Guid VehicleId,
    DateTimeOffset StartsAt) : IDomainEvent;
