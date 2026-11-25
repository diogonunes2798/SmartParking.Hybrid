using SmartParking.Application.Abstractions.Reading;

namespace SmartParking.Api.Features.Reservations;

/// <summary>A reservation on the wire (spec 002).</summary>
internal sealed record ReservationResponse(
    Guid Id,
    Guid SpotId,
    Guid UserId,
    Guid VehicleId,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string Status)
{
    public static ReservationResponse From(ReservationView view) => new(
        view.Id, view.SpotId, view.UserId, view.VehicleId, view.StartsAt, view.EndsAt, view.Status.ToString());
}
