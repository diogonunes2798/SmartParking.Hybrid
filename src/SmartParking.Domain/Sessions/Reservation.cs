using SmartParking.Domain.Common;

namespace SmartParking.Domain.Sessions;

/// <summary>
/// Reservation of a spot for [<see cref="StartsAt"/>, <see cref="EndsAt"/>). Two active reservations of the
/// same spot never overlap (EXCLUDE constraint). Structure only in F2; behaviour arrives in F3 (RF-02).
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

    public static Reservation Create(
        Guid id,
        Guid spotId,
        Guid userId,
        Guid vehicleId,
        DateTimeOffset startsAt,
        DateTimeOffset endsAt)
    {
        if (endsAt <= startsAt)
        {
            throw new ArgumentException("A reservation must end after it starts.", nameof(endsAt));
        }

        return new Reservation
        {
            Id = Guard.Id(id),
            SpotId = Guard.Id(spotId),
            UserId = Guard.Id(userId),
            VehicleId = Guard.Id(vehicleId),
            StartsAt = startsAt,
            EndsAt = endsAt,
            Status = ReservationStatus.Active,
        };
    }
}
