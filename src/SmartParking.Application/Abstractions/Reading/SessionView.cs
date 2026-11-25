using SmartParking.Domain.Sessions;

namespace SmartParking.Application.Abstractions.Reading;

/// <summary>Read model of a parking session. <see cref="Amount"/> is <c>null</c> while the session is <c>Open</c>.</summary>
public sealed record SessionView(
    Guid Id,
    Guid SpotId,
    Guid VehicleId,
    Guid TariffId,
    Guid? ReservationId,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    SessionStatus Status,
    decimal? Amount,
    string Currency)
{
    public static SessionView From(ParkingSession session, string currency)
    {
        ArgumentNullException.ThrowIfNull(session);
        return new SessionView(
            session.Id, session.SpotId, session.VehicleId, session.TariffId, session.ReservationId, session.StartedAt,
            session.EndedAt, session.Status, session.Amount, currency);
    }
}
