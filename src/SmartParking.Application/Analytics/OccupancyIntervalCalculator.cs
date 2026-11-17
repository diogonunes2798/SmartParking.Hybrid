using SmartParking.Domain.Occupancy;

namespace SmartParking.Application.Analytics;

/// <summary>One occupancy event as the aggregation needs it (read from the immutable log, D3).</summary>
public sealed record SpotOccupancyEvent(Guid EventId, Guid SpotId, OccupancyEventType Type, DateTimeOffset Timestamp);

/// <summary>Result of one facility in one closed hour, before it is stored as a summary row.</summary>
/// <param name="OccupiedMilliseconds">Spot-milliseconds occupied in the hour, over the registered spots only.</param>
/// <param name="EventCount">Events of the facility whose sensor timestamp falls in the hour (any spot).</param>
public sealed record HourlyOccupancy(DateTimeOffset HourStart, long OccupiedMilliseconds, int EventCount);

/// <summary>
/// Pure computation of the occupancy of one facility, hour by hour (UC-04, RF-05, ADR-0010). No I/O: the same input
/// always gives the same output, which is what makes re-aggregating a window idempotent.
/// <para>
/// Rules (ADR-0010):
/// <list type="bullet">
/// <item>a spot's state at the start of the range is the type of its last event before the range (within the look-back
/// that the caller used); a spot without such an event starts FREE;</item>
/// <item>events of a spot are ordered by sensor <c>timestamp</c>, then <c>eventId</c> (the order of the read model,
/// ADR-0008); OCCUPIED twice in a row is one continuous occupation, FREED while free changes nothing;</item>
/// <item>an event exactly at an hour boundary belongs to the hour that starts there;</item>
/// <item>only registered spots count for the occupied time (the denominator is the registered spots, so the rate is
/// never above 1); every event of the facility counts for <see cref="HourlyOccupancy.EventCount"/>.</item>
/// </list>
/// </para>
/// </summary>
public static class OccupancyIntervalCalculator
{
    public static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    /// <param name="from">Start of the first hour (UTC, whole hour).</param>
    /// <param name="to">End of the last hour (exclusive; UTC, whole hour).</param>
    /// <param name="registeredSpots">Spots of the facility (denominator and the only ones whose time counts).</param>
    /// <param name="initialStates">Last event of each spot before <paramref name="from"/> (missing = FREE).</param>
    /// <param name="events">Events of the facility with <c>from &lt;= timestamp &lt; to</c>, in any order.</param>
    public static IReadOnlyList<HourlyOccupancy> Compute(
        DateTimeOffset from,
        DateTimeOffset to,
        IReadOnlyCollection<Guid> registeredSpots,
        IReadOnlyDictionary<Guid, OccupancyEventType> initialStates,
        IEnumerable<SpotOccupancyEvent> events)
    {
        ArgumentNullException.ThrowIfNull(registeredSpots);
        ArgumentNullException.ThrowIfNull(initialStates);
        ArgumentNullException.ThrowIfNull(events);
        EnsureWholeHour(from, nameof(from));
        EnsureWholeHour(to, nameof(to));
        if (to <= from)
        {
            throw new ArgumentException("The range must contain at least one hour.", nameof(to));
        }

        var hours = (int)((to - from).Ticks / Hour.Ticks);
        var occupied = new long[hours];
        var counts = new int[hours];
        var fromMs = from.ToUnixTimeMilliseconds();
        var toMs = to.ToUnixTimeMilliseconds();

        var perSpot = new Dictionary<Guid, List<SpotOccupancyEvent>>();
        foreach (var e in events)
        {
            var ms = e.Timestamp.ToUnixTimeMilliseconds();
            if (ms < fromMs || ms >= toMs)
            {
                continue; // outside the range: the caller's query is the contract, but be strict
            }

            counts[HourIndex(ms, fromMs)]++;
            if (!perSpot.TryGetValue(e.SpotId, out var list))
            {
                perSpot[e.SpotId] = list = [];
            }

            list.Add(e);
        }

        foreach (var spot in registeredSpots)
        {
            var occupiedSince = initialStates.TryGetValue(spot, out var initial) && initial == OccupancyEventType.Occupied
                ? fromMs
                : (long?)null;

            if (perSpot.TryGetValue(spot, out var spotEvents))
            {
                spotEvents.Sort(static (a, b) =>
                {
                    var byTime = a.Timestamp.CompareTo(b.Timestamp);
                    return byTime != 0 ? byTime : a.EventId.CompareTo(b.EventId);
                });

                foreach (var e in spotEvents)
                {
                    var ms = e.Timestamp.ToUnixTimeMilliseconds();
                    if (e.Type == OccupancyEventType.Occupied)
                    {
                        occupiedSince ??= ms;
                    }
                    else if (occupiedSince is { } start)
                    {
                        AddInterval(occupied, fromMs, start, ms);
                        occupiedSince = null;
                    }
                }
            }

            if (occupiedSince is { } open)
            {
                AddInterval(occupied, fromMs, open, toMs); // still occupied at the end of the range
            }
        }

        var result = new HourlyOccupancy[hours];
        for (var i = 0; i < hours; i++)
        {
            result[i] = new HourlyOccupancy(from + (Hour * i), occupied[i], counts[i]);
        }

        return result;
    }

    /// <summary>True when <paramref name="value"/> is a whole UTC hour (also whole in Europe/Lisbon, ADR-0010).</summary>
    public static bool IsWholeHour(DateTimeOffset value) => value.UtcTicks % Hour.Ticks == 0;

    /// <summary>The start of the UTC hour that contains <paramref name="value"/>.</summary>
    public static DateTimeOffset FloorHour(DateTimeOffset value) =>
        new(value.UtcTicks - (value.UtcTicks % Hour.Ticks), TimeSpan.Zero);

    /// <summary>The first whole UTC hour at or after <paramref name="value"/>.</summary>
    public static DateTimeOffset CeilHour(DateTimeOffset value) =>
        IsWholeHour(value) ? value.ToUniversalTime() : FloorHour(value) + Hour;

    private static void EnsureWholeHour(DateTimeOffset value, string name)
    {
        if (!IsWholeHour(value))
        {
            throw new ArgumentException("Must be a whole UTC hour.", name);
        }
    }

    private static int HourIndex(long ms, long fromMs) => (int)((ms - fromMs) / (long)Hour.TotalMilliseconds);

    /// <summary>Adds the interval [start, end) to every hour it overlaps.</summary>
    private static void AddInterval(long[] occupied, long fromMs, long start, long end)
    {
        var hourMs = (long)Hour.TotalMilliseconds;
        while (start < end)
        {
            var index = HourIndex(start, fromMs);
            var hourEnd = fromMs + ((index + 1) * hourMs);
            var segmentEnd = Math.Min(end, hourEnd);
            occupied[index] += segmentEnd - start;
            start = segmentEnd;
        }
    }
}
