namespace SmartParking.Api.Features.Availability;

/// <summary>A zone of a facility in <c>GET /api/availability</c>.</summary>
internal sealed record ZoneAvailabilityResponse(Guid ZoneId, string Name, int Free, int Total);
