namespace SmartParking.Api.Features.Sensors;

/// <summary>Body of <c>PUT /api/sensors/{id}/spot</c>.</summary>
internal sealed record AssignSensorRequest(Guid SpotId);
