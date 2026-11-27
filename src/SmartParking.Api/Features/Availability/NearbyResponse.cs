namespace SmartParking.Api.Features.Availability;

/// <summary>Body of <c>GET /api/availability/nearby</c> (spec 004).</summary>
internal sealed record NearbyResponse(IReadOnlyList<NearbyFacilityResponse> Facilities);
