namespace SmartParking.SensorSimulator.Wire;

/// <summary>
/// One event as a gateway sends it. <see cref="SensorId"/> is the sensor's <c>hardwareId</c>; <see cref="Timestamp"/>
/// is ISO 8601 in UTC with <c>Z</c> and millisecond precision (the F4a limit is 7 decimals). No <c>payload</c> in F4b.
/// </summary>
internal sealed record OccupancyEventRequest(Guid EventId, string SensorId, Guid SpotId, string Type, string Timestamp);
