namespace SmartParking.SensorSimulator.Wire;

/// <summary>Response of a spot batch (201): the created ids, in the order of the request.</summary>
internal sealed record SpotBatchResponse(IReadOnlyList<SpotBatchItemResponse> Items);
