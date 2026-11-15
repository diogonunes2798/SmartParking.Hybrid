using System.Text.Json;

namespace SmartParking.Application.Ingestion;

/// <summary>
/// Wire format of an occupancy event on the bus (UC-01, D2). The minimal schema of RF-01 is typed; the extensible
/// <see cref="Payload"/> travels as the raw JSON object the sensor sent (UC-05), so the bus never needs a schema
/// change. <see cref="ReceivedAt"/> is stamped by the API (D7: sensor clock → API → log → read model).
/// </summary>
public sealed record OccupancyEventMessage(
    Guid EventId,
    Guid SensorId,
    Guid SpotId,
    Guid FacilityId,
    string Type,
    DateTimeOffset Timestamp,
    JsonElement Payload,
    DateTimeOffset ReceivedAt)
{
    /// <summary>Topic (Redis stream) of occupancy events. Phase 3: the log writer; Phase 4: the read model.</summary>
    public const string Topic = "smartparking:occupancy-events";

    /// <summary>Wire values of <see cref="Type"/> (spec §3).</summary>
    public const string Occupied = "OCCUPIED";

    /// <inheritdoc cref="Occupied"/>
    public const string Freed = "FREED";

    /// <summary>Serializer options shared by the producer and the consumers (camelCase).</summary>
    public static JsonSerializerOptions SerializerOptions { get; } = new(JsonSerializerDefaults.Web);

    public string ToJson() => JsonSerializer.Serialize(this, SerializerOptions);

    /// <summary>Parses a bus body; returns null when it is not a well-formed occupancy message (a poison message).</summary>
    public static OccupancyEventMessage? TryParse(string json)
    {
        try
        {
            var message = JsonSerializer.Deserialize<OccupancyEventMessage>(json, SerializerOptions);
            return message is { Type: Occupied or Freed, Payload.ValueKind: JsonValueKind.Object }
                && message.EventId != Guid.Empty
                && message.SensorId != Guid.Empty
                && message.SpotId != Guid.Empty
                && message.FacilityId != Guid.Empty
                    ? message
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
