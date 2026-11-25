namespace SmartParking.Api.Features.Spots;

/// <summary>Body of <c>POST /api/facilities/{facilityId}/spots</c>. Nullable fields: a missing one is a 400.</summary>
internal sealed record CreateSpotRequest(Guid ZoneId, string? Code, string? Type);
