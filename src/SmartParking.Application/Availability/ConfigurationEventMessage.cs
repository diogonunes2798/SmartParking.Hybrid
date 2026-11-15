using System.Text.Json;
using System.Text.Json.Serialization;

namespace SmartParking.Application.Availability;

/// <summary>
/// Wire format of a configuration event (RF-06) on the bus: one outbox row (ADR-0004) as the relay of Phase 5 will
/// publish it. <see cref="Payload"/> is the outbox <c>payload</c> column (the domain event serialised in camelCase,
/// enums by name). The read model consumes it in Phase 4; until the relay exists, tests publish it directly.
/// </summary>
public sealed record ConfigurationEventMessage(
    Guid Id, string Type, Guid AggregateId, DateTimeOffset OccurredAt, JsonElement Payload)
{
    /// <summary>Topic (Redis stream) of configuration events relayed from the outbox.</summary>
    public const string Topic = "smartparking:configuration-events";

    /// <summary>Same options as the outbox payload (camelCase, enums as strings).</summary>
    public static JsonSerializerOptions SerializerOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public string ToJson() => JsonSerializer.Serialize(this, SerializerOptions);

    /// <summary>Parses a bus body; null when it is not a well-formed configuration message (a poison message).</summary>
    public static ConfigurationEventMessage? TryParse(string json)
    {
        try
        {
            var message = JsonSerializer.Deserialize<ConfigurationEventMessage>(json, SerializerOptions);
            return message is { Payload.ValueKind: JsonValueKind.Object } && !string.IsNullOrEmpty(message.Type)
                && message.AggregateId != Guid.Empty
                    ? message
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Deserialises <see cref="Payload"/> as <typeparamref name="T"/>; null if it does not fit.</summary>
    public T? PayloadAs<T>()
        where T : class
    {
        try
        {
            return Payload.Deserialize<T>(SerializerOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
