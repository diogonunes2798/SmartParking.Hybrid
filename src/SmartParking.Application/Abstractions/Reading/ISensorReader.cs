using SmartParking.Application.Abstractions.Paging;

namespace SmartParking.Application.Abstractions.Reading;

/// <summary>Read port of the sensor registry queries (D10).</summary>
public interface ISensorReader
{
    Task<SensorView?> GetAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>One page of sensors in registration order.</summary>
    Task<PagedResult<SensorView>> ListAsync(SensorFilter filter, PageRequest page, CancellationToken cancellationToken);
}
