namespace SmartParking.Api.Features.Spots;

/// <summary>Body of <c>PUT /api/facilities/{facilityId}/spots/{spotId}</c>: zone, code, type and status.</summary>
internal sealed record UpdateSpotRequest(Guid ZoneId, string? Code, string? Type, string? Status);
