using SmartParking.Domain.Facilities;

namespace SmartParking.Application.Abstractions.Persistence;

/// <summary>Write port of the sensor aggregate (D10, ADR 0003). No <c>IQueryable</c>, no save.</summary>
public interface ISensorRepository
{
    Task<Sensor?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    void Add(Sensor sensor);

    /// <summary>Adds the sensors of a spot batch; the unit of work saves them all or none (AC-15).</summary>
    void AddRange(IEnumerable<Sensor> sensors);

    void Remove(Sensor sensor);
}
