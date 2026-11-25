using Microsoft.EntityFrameworkCore;
using SmartParking.Application.Abstractions.Paging;
using SmartParking.Application.Abstractions.Reading;
using SmartParking.Domain.Common;
using SmartParking.Domain.Facilities;

namespace SmartParking.Infrastructure.Relational.Reading;

/// <summary>
/// Facility read port over PostgreSQL: <c>AsNoTracking</c> and a projection of the columns the view needs.
/// The location is projected whole (it is a converted value) and split into coordinates in memory.
/// </summary>
internal sealed class FacilityReader(SmartParkingDbContext db) : IFacilityReader
{
    public async Task<FacilityView?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var row = await Project(db.ParkingFacilities.AsNoTracking().Where(facility => facility.Id == id))
            .FirstOrDefaultAsync(cancellationToken);

        return row?.ToView();
    }

    public async Task<PagedResult<FacilityView>> ListAsync(PageRequest page, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);

        var facilities = db.ParkingFacilities.AsNoTracking();
        var totalCount = await facilities.CountAsync(cancellationToken);
        var rows = await Project(facilities.OrderBy(facility => facility.Id).Skip(page.Offset).Take(page.PageSize))
            .ToListAsync(cancellationToken);

        return new PagedResult<FacilityView>([.. rows.Select(row => row.ToView())], page.Page, page.PageSize, totalCount);
    }

    private static IQueryable<FacilityRow> Project(IQueryable<ParkingFacility> facilities) =>
        facilities.Select(facility => new FacilityRow(
            facility.Id,
            facility.OperatorId,
            facility.Name,
            facility.Address,
            facility.Location,
            facility.TimeZone,
            facility.Status,
            facility.OpeningHours
                .OrderBy(hours => hours.DayOfWeek)
                .Select(hours => new OpeningHoursView(hours.DayOfWeek, hours.OpensAt, hours.ClosesAt))
                .ToList(),
            facility.Version));

    private sealed record FacilityRow(
        Guid Id,
        Guid OperatorId,
        string Name,
        string Address,
        GeoCoordinate Location,
        string TimeZone,
        FacilityStatus Status,
        List<OpeningHoursView> OpeningHours,
        uint Version)
    {
        public FacilityView ToView() => new(
            Id, OperatorId, Name, Address, Location.Latitude, Location.Longitude, TimeZone, Status, OpeningHours, Version);
    }
}
