using SmartParking.Domain.Sessions;

namespace SmartParking.Application.Abstractions.Reading;

/// <summary>Read model of a reservation.</summary>
public sealed record ReservationView(
    Guid Id,
    Guid SpotId,
    Guid UserId,
    Guid VehicleId,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    ReservationStatus Status)
{
    public static ReservationView From(Reservation reservation)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        return new ReservationView(
            reservation.Id, reservation.SpotId, reservation.UserId, reservation.VehicleId, reservation.StartsAt,
            reservation.EndsAt, reservation.Status);
    }
}
