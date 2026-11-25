using SmartParking.Application.Abstractions;
using SmartParking.Application.Abstractions.Messaging;
using SmartParking.Application.Abstractions.Paging;
using SmartParking.Application.Abstractions.Reading;

namespace SmartParking.Application.Features.Sensors;

/// <summary>Lists sensors page by page, optionally by facility and kind.</summary>
public static class ListSensors
{
    public sealed record Query(SensorFilter Filter, PageRequest Page);

    internal sealed class Handler(ISensorReader reader) : IQueryHandler<Query, PagedResult<SensorView>>
    {
        public async Task<Result<PagedResult<SensorView>>> HandleAsync(Query query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);

            return await reader.ListAsync(query.Filter, query.Page, cancellationToken);
        }
    }
}
