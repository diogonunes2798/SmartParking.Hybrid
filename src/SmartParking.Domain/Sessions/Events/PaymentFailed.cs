using SmartParking.Domain.Common;

namespace SmartParking.Domain.Sessions.Events;

/// <summary>The payment gateway declined a payment; the session stays Closed and can be paid again.</summary>
[DomainEvent("PaymentFailed", 1)]
public sealed record PaymentFailed(
    Guid PaymentId,
    Guid SessionId,
    Guid SpotId,
    Guid VehicleId,
    Guid PaymentMethodId,
    decimal Amount) : IDomainEvent;
