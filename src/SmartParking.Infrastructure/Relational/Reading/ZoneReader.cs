using Microsoft.EntityFrameworkCore;
using SmartParking.Application.Abstractions.Paging;
using SmartParking.Application.Abstractions.Reading;

namespace SmartParking.Infrastructure.Relational.Reading;

/// <summary>Zone read port over PostgreSQL: <c>AsNoTracking</c> and a projection to the view.</summary>
internal sealed class ZoneReader(SmartParkingDbContext db) : IZoneReader
{
    public Task<ZoneView?> GetAsync(Guid facilityId, Guid zoneId, CancellationToken cancellationToken) =>
        db.Zones.AsNoTracking()
            .Where(zone => zone.Id == zoneId && zone.FacilityId == facilityId)
            .Select(zone => new ZoneView(zone.Id, zone.FacilityId, zone.Name, zone.Version))
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<PagedResult<ZoneView>?> ListAsync(Guid facilityId, PageRequest page, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);

        if (!await db.ParkingFacilities.AnyAsync(facility => facility.Id == facilityId, cancellationToken))
        {
            return null;
        }

        var zones = db.Zones.AsNoTracking().Where(zone => zone.FacilityId == facilityId);
        var totalCount = await zones.CountAsync(cancellationToken);
        var items = await zones.OrderBy(zone => zone.Id).Skip(page.Offset).Take(page.PageSize)
            .Select(zone => new ZoneView(zone.Id, zone.FacilityId, zone.Name, zone.Version))
            .ToListAsync(cancellationToken);

        return new PagedResult<ZoneView>(items, page.Page, page.PageSize, totalCount);
    }
}
