using SmartParking.Application.Abstractions;
using SmartParking.Application.Abstractions.Messaging;
using SmartParking.Application.Abstractions.Paging;
using SmartParking.Application.Abstractions.Reading;
using SmartParking.Application.Features.Facilities;

namespace SmartParking.Application.Features.Zones;

/// <summary>Lists the zones of a facility page by page; 404 when the facility does not exist.</summary>
public static class ListZones
{
    public sealed record Query(Guid FacilityId, PageRequest Page);

    internal sealed class Handler(IZoneReader reader) : IQueryHandler<Query, PagedResult<ZoneView>>
    {
        public async Task<Result<PagedResult<ZoneView>>> HandleAsync(Query query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);

            var page = await reader.ListAsync(query.FacilityId, query.Page, cancellationToken);
            return page is null ? FacilityErrors.NotFound(query.FacilityId) : page;
        }
    }
}
