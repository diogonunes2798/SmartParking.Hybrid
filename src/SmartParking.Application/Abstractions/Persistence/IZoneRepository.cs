using SmartParking.Domain.Facilities;

namespace SmartParking.Application.Abstractions.Persistence;

/// <summary>Write port of the zone aggregate (D10, ADR 0003). No <c>IQueryable</c>, no save.</summary>
public interface IZoneRepository
{
    Task<Zone?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>The ids of every zone of a facility, to check the zones of a spot batch with one query.</summary>
    Task<IReadOnlySet<Guid>> GetIdsByFacilityAsync(Guid facilityId, CancellationToken cancellationToken);

    void Add(Zone zone);

    void Remove(Zone zone);
}
