namespace SmartParking.Api.Features.Reservations;

/// <summary>Body of <c>POST /api/reservations</c>: the spot is reserved for [startsAt, endsAt) (spec 002).</summary>
internal sealed record CreateReservationRequest(
    Guid? SpotId,
    Guid? UserId,
    Guid? VehicleId,
    DateTimeOffset? StartsAt,
    DateTimeOffset? EndsAt);
