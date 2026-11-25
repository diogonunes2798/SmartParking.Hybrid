using SmartParking.Domain.Common;

namespace SmartParking.Domain.Sessions.Events;

/// <summary>A spot was reserved for [<see cref="StartsAt"/>, <see cref="EndsAt"/>). Identifiers and instants only.</summary>
[DomainEvent("ReservationCreated", 1)]
public sealed record ReservationCreated(
    Guid Id,
    Guid SpotId,
    Guid UserId,
    Guid VehicleId,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt) : IDomainEvent;
