using SmartParking.Application.Abstractions;
using SmartParking.Application.Abstractions.Messaging;
using SmartParking.Application.Abstractions.Paging;
using SmartParking.Application.Abstractions.Reading;
using SmartParking.Application.Features.Facilities;

namespace SmartParking.Application.Features.Spots;

/// <summary>Lists the spots of a facility page by page, optionally by zone and status; 404 for an unknown facility (AC-18).</summary>
public static class ListSpots
{
    public sealed record Query(Guid FacilityId, SpotFilter Filter, PageRequest Page);

    internal sealed class Handler(ISpotReader reader) : IQueryHandler<Query, PagedResult<SpotView>>
    {
        public async Task<Result<PagedResult<SpotView>>> HandleAsync(Query query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);

            var page = await reader.ListAsync(query.FacilityId, query.Filter, query.Page, cancellationToken);
            return page is null ? FacilityErrors.NotFound(query.FacilityId) : page;
        }
    }
}
