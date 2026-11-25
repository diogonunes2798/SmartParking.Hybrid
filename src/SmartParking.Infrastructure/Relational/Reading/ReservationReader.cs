using Microsoft.EntityFrameworkCore;
using SmartParking.Application.Abstractions.Reading;
using SmartParking.Domain.Sessions;

namespace SmartParking.Infrastructure.Relational.Reading;

/// <summary>
/// Reservation read port over PostgreSQL: projections and <c>AsNoTracking</c>. The expiry scan has no index of its
/// own (ADR 0007): it reads only identifiers, and each one is expired in its own transaction.
/// </summary>
internal sealed class ReservationReader(SmartParkingDbContext db) : IReservationReader
{
    public Task<ReservationView?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        db.Reservations.AsNoTracking()
            .Where(reservation => reservation.Id == id)
            .Select(reservation => new ReservationView(
                reservation.Id,
                reservation.SpotId,
                reservation.UserId,
                reservation.VehicleId,
                reservation.StartsAt,
                reservation.EndsAt,
                reservation.Status))
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<Guid>> ListDueIdsAsync(DateTimeOffset cutoff, CancellationToken cancellationToken) =>
        await db.Reservations.AsNoTracking()
            .Where(reservation => reservation.Status == ReservationStatus.Active && reservation.StartsAt < cutoff)
            .OrderBy(reservation => reservation.StartsAt)
            .Select(reservation => reservation.Id)
            .ToListAsync(cancellationToken);
}
