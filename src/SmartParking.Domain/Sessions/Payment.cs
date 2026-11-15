using SmartParking.Domain.Common;

namespace SmartParking.Domain.Sessions;

/// <summary>Payment methods. Payments are simulated (real payments are out of scope, spec §10).</summary>
public enum PaymentMethod
{
    Card = 1,
    MbWay = 2,
    Cash = 3,
}

public enum PaymentStatus
{
    Pending = 1,
    Completed = 2,
    Failed = 3,
}

/// <summary>Payment of a parking session (spec §3, UC-02). Amount in EUR.</summary>
public sealed class Payment : IVersioned
{
    private Payment()
    {
    }

    public Guid Id { get; private set; }

    public Guid SessionId { get; private set; }

    public decimal Amount { get; private set; }

    public PaymentMethod Method { get; private set; }

    public PaymentStatus Status { get; private set; }

    public DateTimeOffset Timestamp { get; private set; }

    public uint Version { get; private set; }

    public static Payment Register(Guid sessionId, decimal amount, PaymentMethod method, DateTimeOffset timestamp)
    {
        if (amount < 0)
        {
            throw new DomainValidationException(nameof(amount), "must not be negative");
        }

        return new Payment
        {
            Id = Guid.CreateVersion7(),
            SessionId = Guard.Id(sessionId, nameof(sessionId)),
            Amount = amount,
            Method = Guard.Defined(method, nameof(method)),
            Status = PaymentStatus.Completed,
            Timestamp = Guard.Utc(timestamp),
        };
    }
}
