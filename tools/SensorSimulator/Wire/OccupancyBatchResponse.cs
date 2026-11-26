namespace SmartParking.SensorSimulator.Wire;

/// <summary>Response of an ingestion batch (200). The simulator only keeps the counts.</summary>
internal sealed record OccupancyBatchResponse(int Accepted, int Duplicates, int Rejected);
