using Microsoft.EntityFrameworkCore;
using SmartParking.Application.Abstractions.Paging;
using SmartParking.Application.Abstractions.Reading;
using SmartParking.Domain.Facilities;

namespace SmartParking.Infrastructure.Relational.Reading;

/// <summary>Tariff version read port over PostgreSQL: <c>AsNoTracking</c> and a projection to the view (ADR 0005).</summary>
internal sealed class TariffReader(SmartParkingDbContext db) : ITariffReader
{
    public Task<TariffView?> GetAsync(Guid facilityId, Guid tariffId, CancellationToken cancellationToken) =>
        Project(db.Tariffs.AsNoTracking().Where(tariff => tariff.Id == tariffId && tariff.FacilityId == facilityId))
            .FirstOrDefaultAsync(cancellationToken);

    public Task<TariffView?> GetCurrentAsync(Guid facilityId, DateTimeOffset at, CancellationToken cancellationToken) =>
        Project(db.Tariffs.AsNoTracking().Where(tariff =>
                tariff.FacilityId == facilityId && tariff.ValidFrom <= at && (tariff.ValidTo == null || tariff.ValidTo > at)))
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<PagedResult<TariffView>?> ListAsync(Guid facilityId, PageRequest page, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);

        if (!await db.ParkingFacilities.AnyAsync(facility => facility.Id == facilityId, cancellationToken))
        {
            return null;
        }

        var versions = db.Tariffs.AsNoTracking().Where(tariff => tariff.FacilityId == facilityId);
        var totalCount = await versions.CountAsync(cancellationToken);
        var items = await Project(versions.OrderBy(tariff => tariff.ValidFrom).Skip(page.Offset).Take(page.PageSize))
            .ToListAsync(cancellationToken);

        return new PagedResult<TariffView>(items, page.Page, page.PageSize, totalCount);
    }

    private static IQueryable<TariffView> Project(IQueryable<Tariff> versions) =>
        versions.Select(tariff => new TariffView(
            tariff.Id,
            tariff.FacilityId,
            tariff.ValidFrom,
            tariff.ValidTo,
            tariff.PricePerHour,
            tariff.BillingIncrementMinutes,
            tariff.DailyMaximum,
            tariff.FreeMinutes,
            tariff.Currency,
            tariff.Version));
}
