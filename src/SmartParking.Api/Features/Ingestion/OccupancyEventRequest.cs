using System.Text.Json;

namespace SmartParking.Api.Features.Ingestion;

/// <summary>
/// One occupancy event as the gateway sends it (spec 003). Every field arrives as text so that a malformed value is a
/// 400 with the field's key, not a binding failure. <c>sensorId</c> is the sensor's <c>hardwareId</c>. Unknown fields
/// at this level are ignored; the device's extra data goes in <see cref="Payload"/> (UC-05).
/// </summary>
internal sealed record OccupancyEventRequest(
    string? EventId,
    string? SensorId,
    string? SpotId,
    string? Type,
    string? Timestamp,
    JsonElement? Payload);
