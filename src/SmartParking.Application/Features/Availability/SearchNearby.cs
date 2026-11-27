using SmartParking.Application.Abstractions;
using SmartParking.Application.Abstractions.Availability;
using SmartParking.Application.Abstractions.Messaging;
using SmartParking.Domain.Facilities;

namespace SmartParking.Application.Features.Availability;

/// <summary>
/// Active facilities within a radius of a point (spec 004, «API Contract»; UC-03, RF-04), including those with no free
/// spot. <see cref="NearbySort.Distance"/>: ascending distance. <see cref="NearbySort.Free"/>: most free spots first,
/// ties broken by distance. The facility id breaks any remaining tie, so the order is deterministic. Then the first
/// <see cref="Query.Limit"/>. Reads only the Redis view.
/// </summary>
public static class SearchNearby
{
    public const int DefaultLimit = 20;

    public sealed record Query(
        double Latitude,
        double Longitude,
        double RadiusMeters,
        NearbySort Sort,
        int Limit,
        SpotType? SpotType);

    internal sealed class Handler(IAvailabilityReader reader) : IQueryHandler<Query, IReadOnlyList<NearbyFacility>>
    {
        public async Task<Result<IReadOnlyList<NearbyFacility>>> HandleAsync(Query query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);

            var read = await reader.SearchAsync(query.Latitude, query.Longitude, query.RadiusMeters, query.SpotType, cancellationToken);
            if (read.Error is { } error)
            {
                return error;
            }

            var sorted = query.Sort == NearbySort.Free
                ? read.Value.OrderByDescending(facility => facility.Free).ThenBy(facility => facility.DistanceMeters)
                : read.Value.OrderBy(facility => facility.DistanceMeters);

            return Result.Success<IReadOnlyList<NearbyFacility>>([.. sorted.ThenBy(facility => facility.FacilityId).Take(query.Limit)]);
        }
    }
}
