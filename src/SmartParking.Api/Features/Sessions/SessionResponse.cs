using SmartParking.Application.Abstractions.Reading;

namespace SmartParking.Api.Features.Sessions;

/// <summary>A session on the wire. <see cref="Amount"/> is <c>null</c> while the session is <c>Open</c> (spec 002).</summary>
internal sealed record SessionResponse(
    Guid Id,
    Guid SpotId,
    Guid VehicleId,
    Guid TariffId,
    Guid? ReservationId,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    string Status,
    decimal? Amount,
    string Currency)
{
    public static SessionResponse From(SessionView view) => new(
        view.Id, view.SpotId, view.VehicleId, view.TariffId, view.ReservationId, view.StartedAt, view.EndedAt,
        view.Status.ToString(), view.Amount, view.Currency);
}
