using SmartParking.Application.Abstractions;
using SmartParking.Application.Abstractions.Messaging;
using SmartParking.Application.Abstractions.Reading;

namespace SmartParking.Application.Features.Zones;

/// <summary>Reads one zone of a facility with its version (ETag); 404 when it is not in that facility.</summary>
public static class GetZone
{
    public sealed record Query(Guid FacilityId, Guid ZoneId);

    internal sealed class Handler(IZoneReader reader) : IQueryHandler<Query, ZoneView>
    {
        public async Task<Result<ZoneView>> HandleAsync(Query query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);

            var zone = await reader.GetAsync(query.FacilityId, query.ZoneId, cancellationToken);
            return zone is null ? ZoneErrors.NotFound(query.FacilityId, query.ZoneId) : zone;
        }
    }
}
