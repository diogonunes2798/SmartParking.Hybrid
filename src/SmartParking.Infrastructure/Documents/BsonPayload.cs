using System.Text.Json;
using MongoDB.Bson;

namespace SmartParking.Infrastructure.Documents;

/// <summary>
/// Turns the device's <c>payload</c> (a JSON object, as received) into a BSON subdocument structurally (spec 003, AC-9).
/// <c>BsonDocument.Parse</c> is not used, because it reads MongoDB extended JSON: <c>{"$date": …}</c> or
/// <c>{"$numberLong": …}</c> would be stored as something other than what the device sent.
/// <para>
/// The Api's validator only lets through objects this converter can map: unique member names, no name starting with
/// <c>$</c> or containing NUL, and finite numbers. Numbers become <c>int32</c> or <c>int64</c> when they are integers in
/// range, otherwise <c>double</c>, which is the I-JSON (RFC 7493) precision.
/// </para>
/// </summary>
internal static class BsonPayload
{
    public static BsonDocument FromJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return ToDocument(document.RootElement);
    }

    private static BsonDocument ToDocument(JsonElement element)
    {
        var document = new BsonDocument();
        foreach (var property in element.EnumerateObject())
        {
            // Add, not Set: a repeated name would throw here, but the validator has already rejected it.
            document.Add(property.Name, ToValue(property.Value));
        }

        return document;
    }

    private static BsonValue ToValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => ToDocument(element),
        JsonValueKind.Array => new BsonArray(element.EnumerateArray().Select(ToValue)),
        JsonValueKind.String => new BsonString(element.GetString()),
        JsonValueKind.Number when element.TryGetInt32(out var int32) => new BsonInt32(int32),
        JsonValueKind.Number when element.TryGetInt64(out var int64) => new BsonInt64(int64),
        JsonValueKind.Number => new BsonDouble(element.GetDouble()),
        JsonValueKind.True => BsonBoolean.True,
        JsonValueKind.False => BsonBoolean.False,
        JsonValueKind.Null => BsonNull.Value,
        _ => throw new ArgumentOutOfRangeException(nameof(element), element.ValueKind, "Unexpected JSON value kind."),
    };
}
