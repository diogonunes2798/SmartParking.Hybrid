namespace SmartParking.Api.Features.Spots;

internal sealed record SpotBatchItemResponse(Guid SpotId, string Code, Guid? SensorId);
