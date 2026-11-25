namespace SmartParking.Api.Features.Zones;

/// <summary>Body of <c>POST</c> and <c>PUT /api/facilities/{facilityId}/zones</c>: only the name.</summary>
internal sealed record ZoneRequest(string? Name);
