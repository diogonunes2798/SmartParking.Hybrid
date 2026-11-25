using Microsoft.EntityFrameworkCore;
using SmartParking.Application.Abstractions.Persistence;
using SmartParking.Domain.Facilities;

namespace SmartParking.Infrastructure.Relational.Repositories;

/// <summary>EF Core implementation of the zone write port (D10). Tracked on purpose (the unit of work audits the change).</summary>
internal sealed class ZoneRepository(SmartParkingDbContext db) : IZoneRepository
{
    private static readonly Func<SmartParkingDbContext, Guid, CancellationToken, Task<Zone?>> ById =
        EF.CompileAsyncQuery((SmartParkingDbContext context, Guid id, CancellationToken _) =>
            context.Zones.FirstOrDefault(zone => zone.Id == id));

    public Task<Zone?> GetByIdAsync(Guid id, CancellationToken cancellationToken) => ById(db, id, cancellationToken);

    public async Task<IReadOnlySet<Guid>> GetIdsByFacilityAsync(Guid facilityId, CancellationToken cancellationToken)
    {
        var ids = await db.Zones.AsNoTracking()
            .Where(zone => zone.FacilityId == facilityId)
            .Select(zone => zone.Id)
            .ToListAsync(cancellationToken);

        return ids.ToHashSet();
    }

    public void Add(Zone zone) => db.Zones.Add(zone);

    public void Remove(Zone zone) => db.Zones.Remove(zone);
}
