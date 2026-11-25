using SmartParking.Domain.Common;

namespace SmartParking.Domain.Sessions;

/// <summary>Payment attempt for a session. Structure only in F2; behaviour arrives in F3.</summary>
public sealed class Payment
{
    private Payment()
    {
    }

    public Guid Id { get; private init; }

    public Guid SessionId { get; private init; }

    public Guid PaymentMethodId { get; private init; }

    public decimal Amount { get; private init; }

    public PaymentStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; private init; }

    public static Payment Create(Guid id, Guid sessionId, Guid paymentMethodId, decimal amount, DateTimeOffset createdAt)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(amount);

        return new Payment
        {
            Id = Guard.Id(id),
            SessionId = Guard.Id(sessionId),
            PaymentMethodId = Guard.Id(paymentMethodId),
            Amount = amount,
            Status = PaymentStatus.Pending,
            CreatedAt = createdAt,
        };
    }
}
