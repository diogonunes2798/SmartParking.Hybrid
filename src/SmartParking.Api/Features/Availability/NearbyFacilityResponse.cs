using SmartParking.Application.Abstractions.Availability;

namespace SmartParking.Api.Features.Availability;

/// <summary>A facility found by <c>GET /api/availability/nearby</c>; with <c>spotType</c>, the counts are of that type only.</summary>
internal sealed record NearbyFacilityResponse(
    Guid FacilityId,
    string Name,
    Guid OperatorId,
    double DistanceMeters,
    int Free,
    int Total)
{
    public static NearbyFacilityResponse From(NearbyFacility facility)
    {
        ArgumentNullException.ThrowIfNull(facility);

        return new(facility.FacilityId, facility.Name, facility.OperatorId, facility.DistanceMeters, facility.Free, facility.Total);
    }
}
