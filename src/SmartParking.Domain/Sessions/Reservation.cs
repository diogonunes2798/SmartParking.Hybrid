using SmartParking.Domain.Common;
using SmartParking.Domain.Sessions.Events;

namespace SmartParking.Domain.Sessions;

/// <summary>
/// Reservation of a spot for [<see cref="StartsAt"/>, <see cref="EndsAt"/>). Lifecycle
/// <c>Active → Cancelled | Fulfilled | Expired</c>, all three final (spec 002, UC-02, RF-02). Two active reservations
/// of the same spot never overlap (EXCLUDE constraint). <see cref="Version"/> is the PostgreSQL <c>xmin</c>, checked
/// on every save. The handlers check the preconditions first (400/409); breaking them here is a programming error.
/// </summary>
public sealed class Reservation : AggregateRoot
{
    private Reservation()
    {
    }

    public Guid SpotId { get; private init; }

    public Guid UserId { get; private init; }

    public Guid VehicleId { get; private init; }

    public DateTimeOffset StartsAt { get; private init; }

    public DateTimeOffset EndsAt { get; private init; }

    public ReservationStatus Status { get; private set; }

    public uint Version { get; private set; }

    /// <summary>Creates an <c>Active</c> reservation, with both instants normalised, and raises <see cref="ReservationCreated"/>.</summary>
    public static Reservation Reserve(
        Guid id,
        Guid spotId,
        Guid userId,
        Guid vehicleId,
        DateTimeOffset startsAt,
        DateTimeOffset endsAt)
    {
        var start = Timestamps.Normalize(startsAt);
        var end = Timestamps.Normalize(endsAt);

        if (end <= start)
        {
            throw new ArgumentException("A reservation must end after it starts.", nameof(endsAt));
        }

        var reservation = new Reservation
        {
            Id = Guard.Id(id),
            SpotId = Guard.Id(spotId),
            UserId = Guard.Id(userId),
            VehicleId = Guard.Id(vehicleId),
            StartsAt = start,
            EndsAt = end,
            Status = ReservationStatus.Active,
        };

        reservation.Raise(new ReservationCreated(
            reservation.Id, reservation.SpotId, reservation.UserId, reservation.VehicleId, start, end));
        return reservation;
    }

    /// <summary>Whether a session opened at <paramref name="now"/> on this spot by this vehicle fulfils the reservation.</summary>
    public bool CanBeFulfilledBy(Guid spotId, Guid vehicleId, DateTimeOffset now, ReservationPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        return Status == ReservationStatus.Active
            && SpotId == spotId
            && VehicleId == vehicleId
            && policy.FulfilmentWindow(StartsAt).Contains(now);
    }

    /// <summary>Cancels an active reservation, before or after <see cref="StartsAt"/>. Cancelling again is not idempotent.</summary>
    public void Cancel()
    {
        EnsureActive();
        Status = ReservationStatus.Cancelled;
        Raise(new ReservationCancelled(Id, SpotId, VehicleId, StartsAt));
    }

    /// <summary>Expires an active reservation that is past its grace period without being fulfilled.</summary>
    public void Expire(DateTimeOffset now, ReservationPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        EnsureActive();

        if (!policy.IsDue(StartsAt, now))
        {
            throw new InvalidOperationException("The reservation is still within its fulfilment window.");
        }

        Status = ReservationStatus.Expired;
        Raise(new ReservationExpired(Id, SpotId, VehicleId, StartsAt));
    }

    /// <summary>
    /// Marks the reservation as fulfilled by the session that is opening. Raises no event: the session raises
    /// <c>SessionOpened</c> with this reservation among the changed entities (ADR 0006, ADR 0007).
    /// </summary>
    internal void Fulfil(Guid spotId, Guid vehicleId, DateTimeOffset now, ReservationPolicy policy)
    {
        if (!CanBeFulfilledBy(spotId, vehicleId, now, policy))
        {
            throw new InvalidOperationException("The session does not fulfil this reservation.");
        }

        Status = ReservationStatus.Fulfilled;
    }

    private void EnsureActive()
    {
        if (Status != ReservationStatus.Active)
        {
            throw new InvalidOperationException($"The reservation is {Status}; this transition needs Active.");
        }
    }
}
