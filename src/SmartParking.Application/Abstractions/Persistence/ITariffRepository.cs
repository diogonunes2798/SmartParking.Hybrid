using SmartParking.Domain.Facilities;

namespace SmartParking.Application.Abstractions.Persistence;

/// <summary>Write port of the tariff version aggregate (D10, ADR 0003, ADR 0005). No <c>IQueryable</c>, no save.</summary>
public interface ITariffRepository
{
    Task<Tariff?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>The latest version of a facility (the only open one), or <c>null</c> when it has none.</summary>
    Task<Tariff?> GetLatestAsync(Guid facilityId, CancellationToken cancellationToken);

    /// <summary>
    /// The version of a facility in force at <paramref name="at"/> (valid in [ValidFrom, ValidTo)), or <c>null</c>
    /// (compiled query; read only, not tracked). A session keeps this version until it closes (spec 002).
    /// </summary>
    Task<Tariff?> GetInForceAsync(Guid facilityId, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>The version that <paramref name="version"/> closed when it was created, or <c>null</c> when it was the first.</summary>
    Task<Tariff?> GetClosedByAsync(Tariff version, CancellationToken cancellationToken);

    void Add(Tariff version);

    void Remove(Tariff version);
}
