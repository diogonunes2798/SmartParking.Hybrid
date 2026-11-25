using SmartParking.Domain.Sessions;

namespace SmartParking.Application.Abstractions.Persistence;

/// <summary>
/// Write port of the parking session aggregate: the session and its payments (D10, ADR 0003; spec 002).
/// No <c>IQueryable</c>, no save. A session is tracked, so the unit of work checks its <c>xmin</c>.
/// </summary>
public interface IParkingSessionRepository
{
    Task<ParkingSession?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>The open session of a vehicle, or <c>null</c> (compiled query; read only, not tracked).</summary>
    Task<ParkingSession?> GetOpenByVehicleAsync(Guid vehicleId, CancellationToken cancellationToken);

    void Add(ParkingSession session);

    void AddPayment(Payment payment);
}
