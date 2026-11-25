using Microsoft.EntityFrameworkCore;
using SmartParking.Application.Abstractions.Paging;
using SmartParking.Application.Abstractions.Reading;

namespace SmartParking.Infrastructure.Relational.Reading;

/// <summary>Spot configuration read port over PostgreSQL: <c>AsNoTracking</c> and a projection to the view.</summary>
internal sealed class SpotReader(SmartParkingDbContext db) : ISpotReader
{
    public Task<SpotView?> GetAsync(Guid facilityId, Guid spotId, CancellationToken cancellationToken) =>
        db.ParkingSpots.AsNoTracking()
            .Where(spot => spot.Id == spotId && spot.FacilityId == facilityId)
            .Select(spot => new SpotView(spot.Id, spot.FacilityId, spot.ZoneId, spot.Code, spot.Type, spot.Status, spot.Version))
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<PagedResult<SpotView>?> ListAsync(
        Guid facilityId, SpotFilter filter, PageRequest page, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(page);

        if (!await db.ParkingFacilities.AnyAsync(facility => facility.Id == facilityId, cancellationToken))
        {
            return null;
        }

        var spots = db.ParkingSpots.AsNoTracking().Where(spot => spot.FacilityId == facilityId);
        if (filter.ZoneId is { } zoneId)
        {
            spots = spots.Where(spot => spot.ZoneId == zoneId);
        }

        if (filter.Status is { } status)
        {
            spots = spots.Where(spot => spot.Status == status);
        }

        var totalCount = await spots.CountAsync(cancellationToken);
        var items = await spots.OrderBy(spot => spot.Id).Skip(page.Offset).Take(page.PageSize)
            .Select(spot => new SpotView(spot.Id, spot.FacilityId, spot.ZoneId, spot.Code, spot.Type, spot.Status, spot.Version))
            .ToListAsync(cancellationToken);

        return new PagedResult<SpotView>(items, page.Page, page.PageSize, totalCount);
    }
}
