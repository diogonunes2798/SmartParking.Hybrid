using SmartParking.Application.Abstractions.Paging;

namespace SmartParking.Application.Abstractions.Reading;

/// <summary>
/// Read port of the facility configuration queries (D10: no repository on the read side).
/// Implemented with projections and <c>AsNoTracking</c>; never a high-frequency query.
/// </summary>
public interface IFacilityReader
{
    Task<FacilityView?> GetAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>One page of facilities in creation order (UUID v7), with the total count.</summary>
    Task<PagedResult<FacilityView>> ListAsync(PageRequest page, CancellationToken cancellationToken);
}
