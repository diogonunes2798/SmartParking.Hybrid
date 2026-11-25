namespace SmartParking.Api.Features.Sessions;

/// <summary>
/// Body of <c>POST /api/sessions</c>. A filled <c>reservationId</c> fulfils that reservation in the same transaction
/// (spec 002, AC-25).
/// </summary>
internal sealed record OpenSessionRequest(Guid? SpotId, Guid? VehicleId, Guid? ReservationId);
