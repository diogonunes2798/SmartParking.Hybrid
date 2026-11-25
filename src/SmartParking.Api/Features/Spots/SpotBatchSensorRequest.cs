namespace SmartParking.Api.Features.Spots;

/// <summary>The occupancy sensor of a batch spot.</summary>
internal sealed record SpotBatchSensorRequest(string? HardwareId, string? Model, DateTimeOffset? InstalledAt);
