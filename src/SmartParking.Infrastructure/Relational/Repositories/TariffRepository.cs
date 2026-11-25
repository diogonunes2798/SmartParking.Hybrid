using Microsoft.EntityFrameworkCore;
using SmartParking.Application.Abstractions.Persistence;
using SmartParking.Domain.Facilities;

namespace SmartParking.Infrastructure.Relational.Repositories;

/// <summary>
/// EF Core implementation of the tariff version write port (D10, ADR 0005). Tracked on purpose: closing or reopening
/// the neighbouring version is saved, with its <c>xmin</c> checked, in the same transaction as the change.
/// </summary>
internal sealed class TariffRepository(SmartParkingDbContext db) : ITariffRepository
{
    private static readonly Func<SmartParkingDbContext, Guid, CancellationToken, Task<Tariff?>> ById =
        EF.CompileAsyncQuery((SmartParkingDbContext context, Guid id, CancellationToken _) =>
            context.Tariffs.FirstOrDefault(tariff => tariff.Id == id));

    private static readonly Func<SmartParkingDbContext, Guid, CancellationToken, Task<Tariff?>> LatestOf =
        EF.CompileAsyncQuery((SmartParkingDbContext context, Guid facilityId, CancellationToken _) =>
            context.Tariffs.FirstOrDefault(tariff => tariff.FacilityId == facilityId && tariff.ValidTo == null));

    public Task<Tariff?> GetByIdAsync(Guid id, CancellationToken cancellationToken) => ById(db, id, cancellationToken);

    public Task<Tariff?> GetLatestAsync(Guid facilityId, CancellationToken cancellationToken) =>
        LatestOf(db, facilityId, cancellationToken);

    public Task<Tariff?> GetClosedByAsync(Tariff version, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(version);

        return db.Tariffs.FirstOrDefaultAsync(
            tariff => tariff.FacilityId == version.FacilityId && tariff.ValidTo == version.ValidFrom,
            cancellationToken);
    }

    public void Add(Tariff version) => db.Tariffs.Add(version);

    public void Remove(Tariff version) => db.Tariffs.Remove(version);
}
