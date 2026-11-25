using SmartParking.Domain.Common;

namespace SmartParking.Domain.Sessions.Events;

/// <summary>An active reservation was not fulfilled within its grace period and expired (worker, actor <c>system</c>).</summary>
[DomainEvent("ReservationExpired", 1)]
public sealed record ReservationExpired(
    Guid Id,
    Guid SpotId,
    Guid VehicleId,
    DateTimeOffset StartsAt) : IDomainEvent;
