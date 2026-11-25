namespace SmartParking.Api.Features.Spots;

/// <summary>One spot of a batch; the sensor, when present, is an occupancy sensor placed on it.</summary>
internal sealed record SpotBatchItemRequest(Guid ZoneId, string? Code, string? Type, SpotBatchSensorRequest? Sensor);
