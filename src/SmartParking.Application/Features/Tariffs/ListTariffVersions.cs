using SmartParking.Application.Abstractions;
using SmartParking.Application.Abstractions.Messaging;
using SmartParking.Application.Abstractions.Paging;
using SmartParking.Application.Abstractions.Reading;
using SmartParking.Application.Features.Facilities;

namespace SmartParking.Application.Features.Tariffs;

/// <summary>Lists the tariff versions of a facility, oldest first; 404 when the facility does not exist.</summary>
public static class ListTariffVersions
{
    public sealed record Query(Guid FacilityId, PageRequest Page);

    internal sealed class Handler(ITariffReader reader) : IQueryHandler<Query, PagedResult<TariffView>>
    {
        public async Task<Result<PagedResult<TariffView>>> HandleAsync(Query query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);

            var page = await reader.ListAsync(query.FacilityId, query.Page, cancellationToken);
            return page is null ? FacilityErrors.NotFound(query.FacilityId) : page;
        }
    }
}
