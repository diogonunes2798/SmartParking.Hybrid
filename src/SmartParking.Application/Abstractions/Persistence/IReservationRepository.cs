using SmartParking.Domain.Sessions;

namespace SmartParking.Application.Abstractions.Persistence;

/// <summary>
/// Write port of the reservation aggregate (D10, ADR 0003; spec 002). No <c>IQueryable</c>, no save. A reservation
/// is tracked, so the unit of work checks its <c>xmin</c> (RF-02).
/// </summary>
public interface IReservationRepository
{
    Task<Reservation?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>
    /// Whether the spot has an <c>Active</c> reservation of another vehicle whose fulfilment window contains
    /// <paramref name="now"/> (spot protection, spec 002). Compiled query; read only, not tracked.
    /// </summary>
    Task<bool> IsSpotHeldForAnotherVehicleAsync(
        Guid spotId,
        Guid vehicleId,
        DateTimeOffset now,
        ReservationPolicy policy,
        CancellationToken cancellationToken);

    void Add(Reservation reservation);
}
