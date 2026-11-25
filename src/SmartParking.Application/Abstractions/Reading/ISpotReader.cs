using SmartParking.Application.Abstractions.Paging;

namespace SmartParking.Application.Abstractions.Reading;

/// <summary>Read port of the spot configuration queries (D10). Never the availability query, which reads Redis (F5).</summary>
public interface ISpotReader
{
    /// <summary>The spot, or <c>null</c> when it does not exist in that facility.</summary>
    Task<SpotView?> GetAsync(Guid facilityId, Guid spotId, CancellationToken cancellationToken);

    /// <summary>One page of the facility's spots in creation order, or <c>null</c> when the facility does not exist (AC-18).</summary>
    Task<PagedResult<SpotView>?> ListAsync(Guid facilityId, SpotFilter filter, PageRequest page, CancellationToken cancellationToken);
}
