namespace SmartParking.Application.Abstractions.Payments;

/// <summary>
/// Idempotency key of a charge (spec 002): the same session and payment method give the earlier outcome; another
/// payment method is a new attempt.
/// </summary>
public sealed record PaymentIdempotencyKey(Guid SessionId, Guid PaymentMethodId);
