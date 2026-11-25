namespace SmartParking.Api.Features.Users;

/// <summary>Body of <c>POST /api/users/{id}/vehicles</c>. The plate is normalised: <c>aa-12 bb</c> becomes <c>AA12BB</c>.</summary>
internal sealed record RegisterVehicleRequest(string? LicensePlate);
