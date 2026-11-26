namespace SmartParking.SensorSimulator.Wire;

/// <summary>One spot of a batch with the occupancy sensor placed on it.</summary>
internal sealed record SpotBatchItemRequest(Guid ZoneId, string Code, string Type, SpotBatchSensorRequest Sensor);
