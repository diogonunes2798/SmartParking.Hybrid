using SmartParking.Application.Abstractions;
using SmartParking.Application.Abstractions.Messaging;
using SmartParking.Application.Abstractions.Paging;
using SmartParking.Application.Abstractions.Reading;

namespace SmartParking.Application.Features.Facilities;

/// <summary>Lists facilities page by page (AC-18). The page is validated at the API boundary (1–100 per page).</summary>
public static class ListFacilities
{
    public sealed record Query(PageRequest Page);

    internal sealed class Handler(IFacilityReader reader) : IQueryHandler<Query, PagedResult<FacilityView>>
    {
        public async Task<Result<PagedResult<FacilityView>>> HandleAsync(Query query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);

            return await reader.ListAsync(query.Page, cancellationToken);
        }
    }
}
