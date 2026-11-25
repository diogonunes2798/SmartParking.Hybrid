using Microsoft.EntityFrameworkCore;
using SmartParking.Application.Abstractions.Persistence;
using SmartParking.Domain.Facilities;

namespace SmartParking.Infrastructure.Relational.Repositories;

/// <summary>EF Core implementation of the spot write port (D10). Tracked on purpose (the unit of work audits the change).</summary>
internal sealed class ParkingSpotRepository(SmartParkingDbContext db) : IParkingSpotRepository
{
    private static readonly Func<SmartParkingDbContext, Guid, CancellationToken, Task<ParkingSpot?>> ById =
        EF.CompileAsyncQuery((SmartParkingDbContext context, Guid id, CancellationToken _) =>
            context.ParkingSpots.FirstOrDefault(spot => spot.Id == id));

    public Task<ParkingSpot?> GetByIdAsync(Guid id, CancellationToken cancellationToken) => ById(db, id, cancellationToken);

    public void Add(ParkingSpot spot) => db.ParkingSpots.Add(spot);

    public void AddRange(IEnumerable<ParkingSpot> spots) => db.ParkingSpots.AddRange(spots);

    public void Remove(ParkingSpot spot) => db.ParkingSpots.Remove(spot);
}
