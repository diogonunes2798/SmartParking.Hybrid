using SmartParking.Domain.Facilities;

namespace SmartParking.Application.Abstractions.Persistence;

/// <summary>Write port of the parking spot aggregate (D10, ADR 0003). No <c>IQueryable</c>, no save.</summary>
public interface IParkingSpotRepository
{
    Task<ParkingSpot?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    void Add(ParkingSpot spot);

    /// <summary>Adds the spots of a batch; the unit of work saves them all or none (AC-15).</summary>
    void AddRange(IEnumerable<ParkingSpot> spots);

    void Remove(ParkingSpot spot);
}
