namespace SmartParking.Domain.Common;

/// <summary>
/// Instants as the system keeps them: UTC, truncated to whole microseconds (the precision of PostgreSQL
/// <c>timestamptz</c>). Normalising before use keeps the entity, its events, its audit snapshot and the stored row
/// equal, and lets the handlers check the same value the Domain checks.
/// </summary>
public static class Timestamps
{
    private const long TicksPerMicrosecond = 10;

    public static DateTimeOffset Normalize(DateTimeOffset instant)
    {
        var utc = instant.ToUniversalTime();
        return utc.AddTicks(-(utc.Ticks % TicksPerMicrosecond));
    }
}
