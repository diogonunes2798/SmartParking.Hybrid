using SmartParking.Domain.Common;
using SmartParking.Domain.Users;

namespace SmartParking.Domain.Users.Events;

/// <summary>A user removed a payment method that no payment refers to. Never carries the provider token.</summary>
[DomainEvent("PaymentMethodRemoved", 1)]
public sealed record PaymentMethodRemoved(
    Guid Id,
    Guid UserId,
    PaymentMethodType Type,
    string MaskedNumber,
    DateOnly? ExpiresOn) : IDomainEvent;
