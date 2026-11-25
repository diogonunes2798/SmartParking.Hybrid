using Microsoft.EntityFrameworkCore;
using SmartParking.Application.Abstractions.Persistence;
using SmartParking.Domain.Sessions;

namespace SmartParking.Infrastructure.Relational.Repositories;

/// <summary>
/// EF Core implementation of the reservation write port (D10, spec 002). <see cref="GetByIdAsync"/> is tracked, so
/// the save checks the <c>xmin</c> read here (RF-02, AC-27, AC-28). Spot protection is a precondition of every open
/// without a reservation (hot path of UC-02): a compiled <c>Any</c> without tracking.
/// </summary>
internal sealed class ReservationRepository(SmartParkingDbContext db) : IReservationRepository
{
    private static readonly Func<SmartParkingDbContext, Guid, CancellationToken, Task<Reservation?>> ById =
        EF.CompileAsyncQuery((SmartParkingDbContext context, Guid id, CancellationToken _) =>
            context.Reservations.FirstOrDefault(reservation => reservation.Id == id));

    // startsAt ∈ [now − ExpiryGrace, now + EarlyArrival] is the same as now ∈ [startsAt − EarlyArrival, startsAt + ExpiryGrace].
    private static readonly Func<SmartParkingDbContext, Guid, Guid, DateTimeOffset, DateTimeOffset, CancellationToken, Task<bool>> HeldForAnother =
        EF.CompileAsyncQuery((SmartParkingDbContext context, Guid spotId, Guid vehicleId, DateTimeOffset from, DateTimeOffset to, CancellationToken _) =>
            context.Reservations
                .AsNoTracking()
                .Any(reservation => reservation.SpotId == spotId
                    && reservation.Status == ReservationStatus.Active
                    && reservation.VehicleId != vehicleId
                    && reservation.StartsAt >= from
                    && reservation.StartsAt <= to));

    public Task<Reservation?> GetByIdAsync(Guid id, CancellationToken cancellationToken) => ById(db, id, cancellationToken);

    public Task<bool> IsSpotHeldForAnotherVehicleAsync(
        Guid spotId,
        Guid vehicleId,
        DateTimeOffset now,
        ReservationPolicy policy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return HeldForAnother(db, spotId, vehicleId, now - policy.ExpiryGrace, now + policy.EarlyArrival, cancellationToken);
    }

    public void Add(Reservation reservation) => db.Reservations.Add(reservation);
}
