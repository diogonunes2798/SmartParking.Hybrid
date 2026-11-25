using SmartParking.Application.Abstractions;
using SmartParking.Application.Abstractions.Messaging;
using SmartParking.Application.Abstractions.Reading;

namespace SmartParking.Application.Features.Spots;

/// <summary>Reads one spot of a facility with its version (ETag); 404 when it is not in that facility.</summary>
public static class GetSpot
{
    public sealed record Query(Guid FacilityId, Guid SpotId);

    internal sealed class Handler(ISpotReader reader) : IQueryHandler<Query, SpotView>
    {
        public async Task<Result<SpotView>> HandleAsync(Query query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);

            var spot = await reader.GetAsync(query.FacilityId, query.SpotId, cancellationToken);
            return spot is null ? SpotErrors.NotFound(query.FacilityId, query.SpotId) : spot;
        }
    }
}
