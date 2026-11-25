using Microsoft.EntityFrameworkCore;
using SmartParking.Application.Abstractions.Reading;

namespace SmartParking.Infrastructure.Relational.Reading;

/// <summary>Session read port over PostgreSQL: <c>AsNoTracking</c>, with the currency of the session's tariff version.</summary>
internal sealed class SessionReader(SmartParkingDbContext db) : ISessionReader
{
    public Task<SessionView?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        db.ParkingSessions.AsNoTracking()
            .Where(session => session.Id == id)
            .Join(
                db.Tariffs.AsNoTracking(),
                session => session.TariffId,
                tariff => tariff.Id,
                (session, tariff) => new SessionView(
                    session.Id,
                    session.SpotId,
                    session.VehicleId,
                    session.TariffId,
                    session.ReservationId,
                    session.StartedAt,
                    session.EndedAt,
                    session.Status,
                    session.Amount,
                    tariff.Currency))
            .FirstOrDefaultAsync(cancellationToken);
}
