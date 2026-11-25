namespace SmartParking.Api.Features.Sessions;

/// <summary>
/// Body of <c>POST /api/sessions</c>. Until F3b, a filled <c>reservationId</c> is answered with 409
/// (decision 2 of plan 002a).
/// </summary>
internal sealed record OpenSessionRequest(Guid? SpotId, Guid? VehicleId, Guid? ReservationId);
