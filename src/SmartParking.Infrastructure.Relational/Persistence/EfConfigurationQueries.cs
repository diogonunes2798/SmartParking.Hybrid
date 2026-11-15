using Microsoft.EntityFrameworkCore;
using SmartParking.Application.Configuration;

namespace SmartParking.Infrastructure.Relational.Persistence;

/// <summary>
/// Query side of RF-06: no-tracking reads. Lists are ordered by id, a UUIDv7: creation order to the
/// millisecond (.NET has no counter within a millisecond), and always a total order, so paging is stable.
/// </summary>
internal sealed class EfConfigurationQueries(SmartParkingDbContext context) : IConfigurationQueries
{
    public async Task<FacilityDto?> GetFacilityAsync(Guid id, CancellationToken cancellationToken)
    {
        var facility = await context.Facilities.AsNoTracking().SingleOrDefaultAsync(f => f.Id == id, cancellationToken).ConfigureAwait(false);
        return facility is null ? null : FacilityDto.From(facility);
    }

    public async Task<IReadOnlyList<FacilityDto>> ListFacilitiesAsync(PageRequest page, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);
        var facilities = await context.Facilities.AsNoTracking()
            .OrderBy(f => f.Id).Skip(page.Skip).Take(page.Take)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return facilities.ConvertAll(FacilityDto.From);
    }

    public async Task<SpotDto?> GetSpotAsync(Guid id, CancellationToken cancellationToken)
    {
        var spot = await context.Spots.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, cancellationToken).ConfigureAwait(false);
        return spot is null ? null : SpotDto.From(spot);
    }

    public async Task<IReadOnlyList<SpotDto>> ListSpotsAsync(Guid facilityId, PageRequest page, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);
        var spots = await context.Spots.AsNoTracking()
            .Where(s => s.FacilityId == facilityId)
            .OrderBy(s => s.Id).Skip(page.Skip).Take(page.Take)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return spots.ConvertAll(SpotDto.From);
    }

    public async Task<SensorDto?> GetSensorAsync(Guid id, CancellationToken cancellationToken)
    {
        var sensor = await context.Sensors.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, cancellationToken).ConfigureAwait(false);
        return sensor is null ? null : SensorDto.From(sensor);
    }

    public async Task<IReadOnlyList<SensorDto>> ListSensorsAsync(Guid? spotId, PageRequest page, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);
        var query = context.Sensors.AsNoTracking();
        if (spotId is { } spot)
        {
            query = query.Where(s => s.SpotId == spot);
        }

        var sensors = await query.OrderBy(s => s.Id).Skip(page.Skip).Take(page.Take).ToListAsync(cancellationToken).ConfigureAwait(false);
        return sensors.ConvertAll(SensorDto.From);
    }

    public async Task<TariffDto?> GetTariffAsync(Guid id, CancellationToken cancellationToken)
    {
        var tariff = await context.Tariffs.AsNoTracking().SingleOrDefaultAsync(t => t.Id == id, cancellationToken).ConfigureAwait(false);
        return tariff is null ? null : TariffDto.From(tariff);
    }

    public async Task<IReadOnlyList<TariffDto>> ListTariffsAsync(string? zone, PageRequest page, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);
        var query = context.Tariffs.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(zone))
        {
            var trimmed = zone.Trim();
            query = query.Where(t => t.Zone == trimmed);
        }

        var tariffs = await query.OrderBy(t => t.Id).Skip(page.Skip).Take(page.Take).ToListAsync(cancellationToken).ConfigureAwait(false);
        return tariffs.ConvertAll(TariffDto.From);
    }
}
