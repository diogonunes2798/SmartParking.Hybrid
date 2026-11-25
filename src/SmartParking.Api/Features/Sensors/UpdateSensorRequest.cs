namespace SmartParking.Api.Features.Sensors;

/// <summary>Body of <c>PUT /api/sensors/{id}</c>: model, status and zone. The spot changes through <c>/spot</c>.</summary>
internal sealed record UpdateSensorRequest(string? Model, string? Status, Guid? ZoneId);
