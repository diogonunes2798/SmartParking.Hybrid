using Microsoft.EntityFrameworkCore;
using SmartParking.Application.Abstractions.Paging;
using SmartParking.Application.Abstractions.Reading;
using SmartParking.Domain.Facilities;

namespace SmartParking.Infrastructure.Relational.Reading;

/// <summary>Sensor registry read port over PostgreSQL: <c>AsNoTracking</c> and a projection to the view.</summary>
internal sealed class SensorReader(SmartParkingDbContext db) : ISensorReader
{
    public Task<SensorView?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        Project(db.Sensors.AsNoTracking().Where(sensor => sensor.Id == id)).FirstOrDefaultAsync(cancellationToken);

    public async Task<PagedResult<SensorView>> ListAsync(SensorFilter filter, PageRequest page, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(page);

        var sensors = db.Sensors.AsNoTracking();
        if (filter.FacilityId is { } facilityId)
        {
            sensors = sensors.Where(sensor => sensor.FacilityId == facilityId);
        }

        if (filter.Kind is { } kind)
        {
            sensors = sensors.Where(sensor => sensor.Kind == kind);
        }

        var totalCount = await sensors.CountAsync(cancellationToken);
        var items = await Project(sensors.OrderBy(sensor => sensor.Id).Skip(page.Offset).Take(page.PageSize))
            .ToListAsync(cancellationToken);

        return new PagedResult<SensorView>(items, page.Page, page.PageSize, totalCount);
    }

    private static IQueryable<SensorView> Project(IQueryable<Sensor> sensors) =>
        sensors.Select(sensor => new SensorView(
            sensor.Id,
            sensor.FacilityId,
            sensor.HardwareId,
            sensor.Kind,
            sensor.Model,
            sensor.InstalledAt,
            sensor.Status,
            sensor.SpotId,
            sensor.ZoneId,
            sensor.Version));
}
