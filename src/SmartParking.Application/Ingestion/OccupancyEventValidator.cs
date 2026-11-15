using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SmartParking.Application.Ingestion;

/// <summary>
/// Validates the minimal schema of one occupancy event (RF-01). It works on the raw JSON element, not on a typed
/// DTO, so that one malformed event (e.g. <c>"eventId": "abc"</c>) is reported with its index instead of making
/// the whole batch unreadable. Rules:
/// <list type="bullet">
/// <item><c>eventId</c>, <c>sensorId</c>, <c>spotId</c>, <c>facilityId</c>: non-empty GUID strings;</item>
/// <item><c>type</c>: exactly <c>OCCUPIED</c> or <c>FREED</c>;</item>
/// <item><c>timestamp</c>: ISO 8601 with an explicit offset (<c>Z</c> or <c>±hh:mm</c>); a local time is ambiguous.
/// Not later than the reception plus <see cref="IngestionSettings.MaxClockSkew"/> (Phase 4);</item>
/// <item><c>payload</c>: a JSON object, or absent (stored as <c>{}</c>). Its content is not validated (UC-05).</item>
/// </list>
/// Other top-level properties are ignored: extension data belongs in <c>payload</c>.
/// Existence of the sensor, spot or facility in the relational store is not checked (it would cost one
/// relational query per event on the hot path).
/// </summary>
public static partial class OccupancyEventValidator
{
    private static readonly JsonElement EmptyPayload = JsonDocument.Parse("{}").RootElement.Clone();

    /// <summary>Returns the bus message, or null with the reasons in <paramref name="errors"/>.</summary>
    public static OccupancyEventMessage? Validate(JsonElement item, DateTimeOffset receivedAt, out IReadOnlyList<string> errors) =>
        Validate(item, receivedAt, new IngestionSettings().MaxClockSkew, out errors);

    /// <param name="maxClockSkew">How far after <paramref name="receivedAt"/> the sensor timestamp may be.</param>
    public static OccupancyEventMessage? Validate(
        JsonElement item, DateTimeOffset receivedAt, TimeSpan maxClockSkew, out IReadOnlyList<string> errors)
    {
        var problems = new List<string>();
        errors = problems;

        if (item.ValueKind != JsonValueKind.Object)
        {
            problems.Add("Each event must be a JSON object.");
            return null;
        }

        var eventId = RequiredGuid(item, "eventId", problems);
        var sensorId = RequiredGuid(item, "sensorId", problems);
        var spotId = RequiredGuid(item, "spotId", problems);
        var facilityId = RequiredGuid(item, "facilityId", problems);
        var type = RequiredType(item, problems);
        var timestamp = RequiredTimestamp(item, problems);
        var payload = OptionalPayload(item, problems);
        if (timestamp != default && timestamp > receivedAt + maxClockSkew)
        {
            problems.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"'timestamp' is more than {maxClockSkew.TotalSeconds:F0} s in the future (sensor clock ahead of the server)."));
        }

        return problems.Count == 0
            ? new OccupancyEventMessage(eventId, sensorId, spotId, facilityId, type!, timestamp, payload, receivedAt)
            : null;
    }

    /// <summary>Reads <c>eventId</c> when it is a valid GUID (to echo it in a rejection), otherwise null.</summary>
    public static Guid? TryReadEventId(JsonElement item) =>
        item.ValueKind == JsonValueKind.Object
        && item.TryGetProperty("eventId", out var value)
        && value.ValueKind == JsonValueKind.String
        && value.TryGetGuid(out var id)
            ? id
            : null;

    private static Guid RequiredGuid(JsonElement item, string name, List<string> problems)
    {
        if (!item.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            problems.Add($"'{name}' is required.");
            return Guid.Empty;
        }

        if (value.ValueKind != JsonValueKind.String || !value.TryGetGuid(out var id) || id == Guid.Empty)
        {
            problems.Add($"'{name}' must be a non-empty GUID string.");
            return Guid.Empty;
        }

        return id;
    }

    private static string? RequiredType(JsonElement item, List<string> problems)
    {
        if (!item.TryGetProperty("type", out var value) || value.ValueKind == JsonValueKind.Null)
        {
            problems.Add("'type' is required.");
            return null;
        }

        var text = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        if (text is OccupancyEventMessage.Occupied or OccupancyEventMessage.Freed)
        {
            return text;
        }

        problems.Add($"'type' must be '{OccupancyEventMessage.Occupied}' or '{OccupancyEventMessage.Freed}'.");
        return null;
    }

    private static DateTimeOffset RequiredTimestamp(JsonElement item, List<string> problems)
    {
        if (!item.TryGetProperty("timestamp", out var value) || value.ValueKind == JsonValueKind.Null)
        {
            problems.Add("'timestamp' is required.");
            return default;
        }

        // TryGetDateTimeOffset enforces ISO 8601; the regex rejects values without an explicit offset,
        // which System.Text.Json would otherwise interpret in the server's local time zone.
        if (value.ValueKind != JsonValueKind.String
            || !ExplicitOffset().IsMatch(value.GetString()!)
            || !value.TryGetDateTimeOffset(out var timestamp))
        {
            problems.Add("'timestamp' must be an ISO 8601 date-time with an explicit offset (e.g. 2026-11-14T10:00:00Z).");
            return default;
        }

        return timestamp.ToUniversalTime();
    }

    private static JsonElement OptionalPayload(JsonElement item, List<string> problems)
    {
        if (!item.TryGetProperty("payload", out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return EmptyPayload;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            problems.Add("'payload' must be a JSON object when present.");
            return EmptyPayload;
        }

        // Clone: the element must outlive the request's JsonDocument.
        return value.Clone();
    }

    [GeneratedRegex(@"(Z|z|[+-]\d{2}:\d{2})$", RegexOptions.CultureInvariant)]
    private static partial Regex ExplicitOffset();
}
