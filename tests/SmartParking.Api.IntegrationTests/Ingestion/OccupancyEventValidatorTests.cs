using System.Text.Json;
using SmartParking.Application.Ingestion;

namespace SmartParking.Api.IntegrationTests.Ingestion;

/// <summary>RF-01 minimal schema, one event at a time (no containers).</summary>
public sealed class OccupancyEventValidatorTests
{
    private static readonly DateTimeOffset ReceivedAt = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

    private const string Valid =
        """
        {"eventId":"11111111-1111-1111-1111-111111111111","sensorId":"22222222-2222-2222-2222-222222222222",
         "spotId":"33333333-3333-3333-3333-333333333333","facilityId":"44444444-4444-4444-4444-444444444444",
         "type":"FREED","timestamp":"2026-11-14T11:00:00+01:00","payload":{"temperature":21.5}}
        """;

    [Fact]
    public void Valid_event_is_normalised_to_utc_and_keeps_the_payload()
    {
        var message = Validate(Valid, out var errors);

        Assert.Empty(errors);
        Assert.NotNull(message);
        Assert.Equal(TimeSpan.Zero, message.Timestamp.Offset);
        Assert.Equal(new DateTimeOffset(2026, 11, 14, 10, 0, 0, TimeSpan.Zero), message.Timestamp);
        Assert.Equal("FREED", message.Type);
        Assert.Equal(21.5, message.Payload.GetProperty("temperature").GetDouble());
        Assert.Equal(ReceivedAt, message.ReceivedAt);
    }

    [Fact]
    public void Missing_payload_becomes_an_empty_object()
    {
        var message = Validate(Valid.Replace(",\"payload\":{\"temperature\":21.5}", string.Empty, StringComparison.Ordinal), out _);

        Assert.Equal(JsonValueKind.Object, message!.Payload.ValueKind);
        Assert.Empty(message.Payload.EnumerateObject());
    }

    [Theory]
    [InlineData("\"eventId\":\"11111111-1111-1111-1111-111111111111\"", "\"eventId\":\"abc\"", "'eventId' must be a non-empty GUID string.")]
    [InlineData("\"eventId\":\"11111111-1111-1111-1111-111111111111\"", "\"eventId\":\"00000000-0000-0000-0000-000000000000\"", "'eventId' must be a non-empty GUID string.")]
    [InlineData("\"sensorId\":\"22222222-2222-2222-2222-222222222222\"", "\"sensorId\":null", "'sensorId' is required.")]
    [InlineData("\"type\":\"FREED\"", "\"type\":\"freed\"", "'type' must be 'OCCUPIED' or 'FREED'.")]
    [InlineData("\"type\":\"FREED\"", "\"type\":1", "'type' must be 'OCCUPIED' or 'FREED'.")]
    [InlineData("\"timestamp\":\"2026-11-14T11:00:00+01:00\"", "\"timestamp\":\"2026-11-14T11:00:00\"", "'timestamp' must be an ISO 8601 date-time with an explicit offset (e.g. 2026-11-14T10:00:00Z).")]
    [InlineData("\"timestamp\":\"2026-11-14T11:00:00+01:00\"", "\"timestamp\":\"14/11/2026 11:00 Z\"", "'timestamp' must be an ISO 8601 date-time with an explicit offset (e.g. 2026-11-14T10:00:00Z).")]
    [InlineData("\"payload\":{\"temperature\":21.5}", "\"payload\":[1,2]", "'payload' must be a JSON object when present.")]
    public void Invalid_field_is_reported(string original, string replacement, string expected)
    {
        var message = Validate(Valid.Replace(original, replacement, StringComparison.Ordinal), out var errors);

        Assert.Null(message);
        Assert.Equal([expected], errors);
    }

    [Fact]
    public void Non_object_item_is_rejected()
    {
        Assert.Null(Validate("42", out var errors));
        Assert.Equal(["Each event must be a JSON object."], errors);
    }

    [Fact]
    public void Bus_message_round_trips_and_malformed_bodies_are_poison()
    {
        var message = Validate(Valid, out _)!;

        var parsed = OccupancyEventMessage.TryParse(message.ToJson());

        Assert.NotNull(parsed);
        Assert.Equal(message.EventId, parsed.EventId);
        Assert.Equal(message.Timestamp, parsed.Timestamp);
        Assert.Equal(message.Payload.GetRawText(), parsed.Payload.GetRawText());
        Assert.Null(OccupancyEventMessage.TryParse("{not json"));
        Assert.Null(OccupancyEventMessage.TryParse("{\"eventId\":\"11111111-1111-1111-1111-111111111111\",\"type\":\"OTHER\"}"));
    }

    private static OccupancyEventMessage? Validate(string json, out IReadOnlyList<string> errors)
    {
        using var document = JsonDocument.Parse(json);
        return OccupancyEventValidator.Validate(document.RootElement, ReceivedAt, out errors);
    }
}
