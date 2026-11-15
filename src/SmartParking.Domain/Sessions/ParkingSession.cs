using SmartParking.Domain.Common;

namespace SmartParking.Domain.Sessions;

public enum ParkingSessionStatus
{
    Open = 1,
    Closed = 2,
    Paid = 3,
}

/// <summary>
/// Parking session (spec §3, UC-02). Where it is stored is decision D1 (default: relational, behind
/// <c>IParkingSessionRepository</c>; ADR-0005). State machine: Open → Closed → Paid. Every transition raises a
/// domain event that is written to the outbox in the same transaction (ADR-0004).
/// </summary>
public sealed class ParkingSession : IVersioned, IHasDomainEvents
{
    private readonly List<IDomainEvent> _domainEvents = [];

    private ParkingSession()
    {
    }

    public Guid Id { get; private set; }

    public Guid SpotId { get; private set; }

    public Guid VehicleId { get; private set; }

    public Guid UserId { get; private set; }

    public DateTimeOffset StartedAt { get; private set; }

    public DateTimeOffset? EndedAt { get; private set; }

    /// <summary>Tariff applied when the session was closed.</summary>
    public Guid? TariffId { get; private set; }

    /// <summary>Amount due (EUR), known once the session is closed.</summary>
    public decimal? Cost { get; private set; }

    public ParkingSessionStatus Status { get; private set; }

    public uint Version { get; private set; }

    public static ParkingSession Open(Guid spotId, Guid vehicleId, Guid userId, DateTimeOffset startedAt)
    {
        var session = new ParkingSession
        {
            Id = Guid.CreateVersion7(),
            SpotId = Guard.Id(spotId, nameof(spotId)),
            VehicleId = Guard.Id(vehicleId, nameof(vehicleId)),
            UserId = Guard.Id(userId, nameof(userId)),
            StartedAt = Guard.Utc(startedAt),
            Status = ParkingSessionStatus.Open,
        };
        session._domainEvents.Add(new SessionStarted(session.Id, session.SpotId, session.StartedAt));
        return session;
    }

    /// <summary>Fails fast, before the tariff is looked up, when the session can no longer be closed.</summary>
    public void EnsureCanClose()
    {
        if (Status != ParkingSessionStatus.Open)
        {
            throw new InvalidStateTransitionException($"Cannot close session '{Id}' in state {Status}.");
        }
    }

    /// <param name="endedAt">End instant.</param>
    /// <param name="tariffId">Tariff in force, used to compute <paramref name="cost"/>.</param>
    /// <param name="cost">Amount due in EUR; rounded to cents (Money).</param>
    public void Close(DateTimeOffset endedAt, Guid tariffId, decimal cost)
    {
        EnsureCanClose();

        var end = Guard.Utc(endedAt);
        if (end < StartedAt)
        {
            throw new DomainValidationException(nameof(endedAt), "must not be earlier than startedAt");
        }

        EndedAt = end;
        TariffId = Guard.Id(tariffId, nameof(tariffId));
        Cost = Money.Normalise(cost, nameof(cost));
        Status = ParkingSessionStatus.Closed;
        _domainEvents.Add(new SessionClosed(Id, SpotId, TariffId.Value, StartedAt, Cost.Value, end));
    }

    /// <summary>Registers the payment of the amount due. Only a closed session can be paid (UC-02).</summary>
    public Payment Pay(PaymentMethod method, DateTimeOffset paidAt)
    {
        if (Status != ParkingSessionStatus.Closed)
        {
            throw new InvalidStateTransitionException($"Cannot pay session '{Id}' in state {Status}.");
        }

        var payment = Payment.Register(Id, Cost!.Value, method, paidAt);
        Status = ParkingSessionStatus.Paid;
        _domainEvents.Add(new PaymentRegistered(payment.Id, Id, payment.Amount, payment.Method, payment.Timestamp));
        return payment;
    }

    public IReadOnlyList<IDomainEvent> DequeueDomainEvents()
    {
        var events = _domainEvents.ToArray();
        _domainEvents.Clear();
        return events;
    }
}
