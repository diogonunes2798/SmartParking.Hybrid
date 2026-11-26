using System.Text.Json;

namespace SmartParking.Api.Features.Ingestion;

/// <summary>
/// Content rules of the <c>payload</c> object (spec 003, «payload extensível»; review of F4a, finding 1). The payload
/// is stored as received, so it must be portable JSON (I-JSON, RFC 7493): unique member names, numbers that fit in a
/// finite <c>double</c>, and valid Unicode text. Member names also cannot start with <c>$</c> or contain NUL, because
/// MongoDB reserves them. Anything else is a schema fault on <c>events[i].payload</c> (400 for the batch), never a 500.
/// </summary>
internal static class PayloadRules
{
    /// <summary>The first rule broken, or <c>null</c> when the payload can be stored as received.</summary>
    public static string? Violation(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                HashSet<string> names = new(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    if (!TryReadName(property, out var name))
                    {
                        return "'payload' member names must be valid Unicode.";
                    }

                    if (!names.Add(name))
                    {
                        return $"'payload' repeats the member '{name}'.";
                    }

                    if (name.StartsWith('$') || name.Contains('\0', StringComparison.Ordinal))
                    {
                        return "'payload' member names cannot start with '$' or contain NUL.";
                    }

                    if (Violation(property.Value) is { } nested)
                    {
                        return nested;
                    }
                }

                return null;

            case JsonValueKind.Array:
                return element.EnumerateArray().Select(Violation).FirstOrDefault(violation => violation is not null);

            case JsonValueKind.Number:
                return element.TryGetDouble(out var number) && double.IsFinite(number)
                    ? null
                    : "'payload' numbers must fit in a finite double.";

            case JsonValueKind.String:
                return TryReadString(element) ? null : "'payload' strings must be valid Unicode.";

            default:
                return null;
        }
    }

    /// <summary>A lone surrogate escape (<c>\uD800</c>) is valid JSON syntax but cannot be read as a .NET string.</summary>
    private static bool TryReadName(JsonProperty property, out string name)
    {
        try
        {
            name = property.Name;
            return true;
        }
        catch (InvalidOperationException)
        {
            name = string.Empty;
            return false;
        }
    }

    private static bool TryReadString(JsonElement element)
    {
        try
        {
            _ = element.GetString();
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
