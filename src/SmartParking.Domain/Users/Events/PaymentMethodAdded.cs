using SmartParking.Domain.Common;
using SmartParking.Domain.Users;

namespace SmartParking.Domain.Users.Events;

/// <summary>A user added a payment method. The provider token is never in the payload (spec 002, AC-4).</summary>
[DomainEvent("PaymentMethodAdded", 1)]
public sealed record PaymentMethodAdded(
    Guid Id,
    Guid UserId,
    PaymentMethodType Type,
    string MaskedNumber,
    DateOnly? ExpiresOn) : IDomainEvent;
