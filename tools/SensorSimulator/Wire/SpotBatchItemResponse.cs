namespace SmartParking.SensorSimulator.Wire;

/// <summary>One created spot and its sensor.</summary>
internal sealed record SpotBatchItemResponse(Guid SpotId, string Code, Guid? SensorId);
