using SmartParking.Domain.Common;
using SmartParking.Domain.Facilities;
using SmartParking.Domain.Sessions.Events;

namespace SmartParking.Domain.Sessions;

/// <summary>
/// Parking session of a vehicle on a spot, priced by the tariff version in force when it opened (ADR 0005).
/// Lifecycle <c>Open → Closed → Paid</c> (UC-02, RF-02): no method leads back from <c>Paid</c> or from
/// <c>Closed</c> to <c>Open</c>. <see cref="Version"/> is the PostgreSQL <c>xmin</c>, checked on every save.
/// The handlers check the preconditions first (409); breaking them here is a programming error.
/// </summary>
public sealed class ParkingSession : AggregateRoot
{
    private ParkingSession()
    {
    }

    public Guid SpotId { get; private init; }

    public Guid VehicleId { get; private init; }

    public Guid TariffId { get; private init; }

    public Guid? ReservationId { get; private init; }

    public DateTimeOffset StartedAt { get; private init; }

    public DateTimeOffset? EndedAt { get; private set; }

    public SessionStatus Status { get; private set; }

    /// <summary><c>null</c> while the session is <c>Open</c>.</summary>
    public decimal? Amount { get; private set; }

    public uint Version { get; private set; }

    public static ParkingSession Open(
        Guid id,
        Guid spotId,
        Guid vehicleId,
        Guid tariffId,
        Guid? reservationId,
        DateTimeOffset now)
    {
        var session = new ParkingSession
        {
            Id = Guard.Id(id),
            SpotId = Guard.Id(spotId),
            VehicleId = Guard.Id(vehicleId),
            TariffId = Guard.Id(tariffId),
            ReservationId = reservationId is { } reservation ? Guard.Id(reservation) : null,
            StartedAt = Timestamps.Normalize(now),
            Status = SessionStatus.Open,
        };

        session.Raise(new SessionOpened(
            session.Id, session.SpotId, session.VehicleId, session.TariffId, session.ReservationId, session.StartedAt));
        return session;
    }

    /// <summary>Closes an open session at <paramref name="now"/> and prices it with the session's own tariff version.</summary>
    public void Close(Tariff tariff, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(tariff);
        EnsureStatus(SessionStatus.Open);

        if (tariff.Id != TariffId)
        {
            throw new ArgumentException("A session is priced with the tariff version it opened with.", nameof(tariff));
        }

        var endedAt = Timestamps.Normalize(now);
        ArgumentOutOfRangeException.ThrowIfLessThan(endedAt, StartedAt, nameof(now));

        EndedAt = endedAt;
        Amount = SessionPricing.Calculate(tariff, endedAt - StartedAt);
        Status = SessionStatus.Closed;

        Raise(new SessionClosed(Id, SpotId, VehicleId, TariffId, StartedAt, endedAt, Amount.Value));
    }

    /// <summary>
    /// Records the gateway's answer for a closed session. Approved: a <c>Succeeded</c> payment and the session
    /// <c>Paid</c>, one event that changed both. Declined: a <c>Failed</c> payment, the session stays <c>Closed</c>
    /// and can be paid again. A 0.00 session is recorded as approved without calling the gateway (the handler's call).
    /// </summary>
    public Payment RecordPayment(Guid paymentId, Guid paymentMethodId, bool approved, DateTimeOffset now)
    {
        EnsureStatus(SessionStatus.Closed);
        var amount = Amount!.Value;

        if (!approved)
        {
            var failed = Payment.Failed(paymentId, Id, paymentMethodId, amount, Timestamps.Normalize(now));
            Raise(new PaymentFailed(failed.Id, Id, SpotId, VehicleId, paymentMethodId, amount), failed);
            return failed;
        }

        var succeeded = Payment.Succeeded(paymentId, Id, paymentMethodId, amount, Timestamps.Normalize(now));
        Status = SessionStatus.Paid;
        Raise(new SessionPaid(Id, SpotId, VehicleId, succeeded.Id, paymentMethodId, amount), succeeded, this);
        return succeeded;
    }

    private void EnsureStatus(SessionStatus expected)
    {
        if (Status != expected)
        {
            throw new InvalidOperationException($"The session is {Status}; this transition needs {expected}.");
        }
    }
}
