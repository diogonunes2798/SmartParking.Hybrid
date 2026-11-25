namespace SmartParking.Domain.Common;

/// <summary>IANA time zone identifiers (for example <c>Europe/Lisbon</c>). Windows identifiers are rejected.</summary>
public static class IanaTimeZone
{
    public static bool IsValid(string? id) =>
        !string.IsNullOrWhiteSpace(id)
        && TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone)
        && zone.HasIanaId
        && string.Equals(zone.Id, id, StringComparison.Ordinal);
}
