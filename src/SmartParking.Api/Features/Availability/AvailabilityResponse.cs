namespace SmartParking.Api.Features.Availability;

/// <summary>Body of <c>GET /api/availability</c> (spec 004).</summary>
internal sealed record AvailabilityResponse(IReadOnlyList<FacilityAvailabilityResponse> Facilities);
