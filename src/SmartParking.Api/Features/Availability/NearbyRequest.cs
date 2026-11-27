using SmartParking.Api.Common;
using SmartParking.Application.Features.Availability;
using SmartParking.Domain.Facilities;

namespace SmartParking.Api.Features.Availability;

/// <summary>
/// Query string of <c>GET /api/availability/nearby</c> (spec 004). Every member is nullable so that a missing value is
/// reported by the validator as ValidationProblemDetails, not by the binder.
/// </summary>
internal sealed record NearbyRequest(double? Lat, double? Lon, double? RadiusMeters, string? Sort, int? Limit, string? SpotType)
{
    public const string SortByDistance = "distance";
    public const string SortByFree = "free";

    public SearchNearby.Query ToQuery() => new(
        Lat!.Value,
        Lon!.Value,
        RadiusMeters!.Value,
        Sort == SortByFree ? NearbySort.Free : NearbySort.Distance,
        Limit ?? SearchNearby.DefaultLimit,
        EnumRules.ParseOptional<SpotType>(SpotType));
}
