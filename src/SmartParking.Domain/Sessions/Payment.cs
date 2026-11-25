using SmartParking.Domain.Common;

namespace SmartParking.Domain.Sessions;

/// <summary>
/// Payment attempt for a closed session, part of the session aggregate. The gateway is called before the transaction
/// (spec 002), so a payment is written once, already <c>Succeeded</c> or <c>Failed</c>; <c>Pending</c> is not used in
/// F3. At most one <c>Succeeded</c> payment per session (partial unique index).
/// </summary>
public sealed class Payment
{
    private Payment()
    {
    }

    public Guid Id { get; private init; }

    public Guid SessionId { get; private init; }

    public Guid PaymentMethodId { get; private init; }

    public decimal Amount { get; private init; }

    public PaymentStatus Status { get; private init; }

    public DateTimeOffset CreatedAt { get; private init; }

    internal static Payment Succeeded(Guid id, Guid sessionId, Guid paymentMethodId, decimal amount, DateTimeOffset createdAt) =>
        Create(id, sessionId, paymentMethodId, amount, PaymentStatus.Succeeded, createdAt);

    internal static Payment Failed(Guid id, Guid sessionId, Guid paymentMethodId, decimal amount, DateTimeOffset createdAt) =>
        Create(id, sessionId, paymentMethodId, amount, PaymentStatus.Failed, createdAt);

    private static Payment Create(
        Guid id,
        Guid sessionId,
        Guid paymentMethodId,
        decimal amount,
        PaymentStatus status,
        DateTimeOffset createdAt)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(amount);

        return new Payment
        {
            Id = Guard.Id(id),
            SessionId = Guard.Id(sessionId),
            PaymentMethodId = Guard.Id(paymentMethodId),
            Amount = amount,
            Status = status,
            CreatedAt = createdAt,
        };
    }
}
