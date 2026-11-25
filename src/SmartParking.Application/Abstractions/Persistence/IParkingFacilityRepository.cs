using SmartParking.Domain.Facilities;

namespace SmartParking.Application.Abstractions.Persistence;

/// <summary>
/// Write port of the parking facility aggregate (D10, ADR 0003). Domain-shaped methods only: no
/// <c>IQueryable</c>, no save. The opening hours come with the facility; <see cref="IUnitOfWork"/> saves.
/// </summary>
public interface IParkingFacilityRepository
{
    Task<ParkingFacility?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Whether the facility exists, for the 404 of the commands on its zones, spots, sensors and tariffs.</summary>
    Task<bool> ExistsAsync(Guid id, CancellationToken cancellationToken);

    void Add(ParkingFacility facility);

    void Remove(ParkingFacility facility);
}
