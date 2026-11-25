using Microsoft.EntityFrameworkCore;
using SmartParking.Application.Abstractions.Persistence;
using SmartParking.Domain.Facilities;

namespace SmartParking.Infrastructure.Relational.Repositories;

/// <summary>
/// EF Core implementation of the facility write port (D10). Tracked on purpose: the unit of work reads the
/// change tracker for the audit entry. The opening hours are owned, so they load with the facility.
/// Same implementation in both persistence modes.
/// </summary>
internal sealed class ParkingFacilityRepository(SmartParkingDbContext db) : IParkingFacilityRepository
{
    private static readonly Func<SmartParkingDbContext, Guid, CancellationToken, Task<ParkingFacility?>> ById =
        EF.CompileAsyncQuery((SmartParkingDbContext context, Guid id, CancellationToken _) =>
            context.ParkingFacilities.FirstOrDefault(facility => facility.Id == id));

    public Task<ParkingFacility?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        ById(db, id, cancellationToken);

    public void Add(ParkingFacility facility) => db.ParkingFacilities.Add(facility);

    public void Remove(ParkingFacility facility) => db.ParkingFacilities.Remove(facility);
}
