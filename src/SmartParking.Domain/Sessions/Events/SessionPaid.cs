using SmartParking.Domain.Common;

namespace SmartParking.Domain.Sessions.Events;

/// <summary>A closed session was paid: the payment and the session changed in the same transaction.</summary>
[DomainEvent("SessionPaid", 1)]
public sealed record SessionPaid(
    Guid Id,
    Guid SpotId,
    Guid VehicleId,
    Guid PaymentId,
    Guid PaymentMethodId,
    decimal Amount) : IDomainEvent;
