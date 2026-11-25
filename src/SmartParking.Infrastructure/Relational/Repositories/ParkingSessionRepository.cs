using Microsoft.EntityFrameworkCore;
using SmartParking.Application.Abstractions.Persistence;
using SmartParking.Domain.Sessions;

namespace SmartParking.Infrastructure.Relational.Repositories;

/// <summary>
/// EF Core implementation of the session aggregate write port (D10, spec 002). <see cref="GetByIdAsync"/> is tracked,
/// so the save checks the <c>xmin</c> read here (RF-02). The open session of a vehicle is a hot, read-only check:
/// a compiled query without tracking, served by the partial unique index <c>ux_parking_sessions_vehicle_open</c>.
/// </summary>
internal sealed class ParkingSessionRepository(SmartParkingDbContext db) : IParkingSessionRepository
{
    private static readonly Func<SmartParkingDbContext, Guid, CancellationToken, Task<ParkingSession?>> ById =
        EF.CompileAsyncQuery((SmartParkingDbContext context, Guid id, CancellationToken _) =>
            context.ParkingSessions.FirstOrDefault(session => session.Id == id));

    private static readonly Func<SmartParkingDbContext, Guid, CancellationToken, Task<ParkingSession?>> OpenOfVehicle =
        EF.CompileAsyncQuery((SmartParkingDbContext context, Guid vehicleId, CancellationToken _) =>
            context.ParkingSessions
                .AsNoTracking()
                .FirstOrDefault(session => session.VehicleId == vehicleId && session.Status == SessionStatus.Open));

    public Task<ParkingSession?> GetByIdAsync(Guid id, CancellationToken cancellationToken) => ById(db, id, cancellationToken);

    public Task<ParkingSession?> GetOpenByVehicleAsync(Guid vehicleId, CancellationToken cancellationToken) =>
        OpenOfVehicle(db, vehicleId, cancellationToken);

    public void Add(ParkingSession session) => db.ParkingSessions.Add(session);

    public void AddPayment(Payment payment) => db.Payments.Add(payment);
}
