using System.Globalization;
using SmartParking.Domain.Common;
using SmartParking.Domain.Facilities;

namespace SmartParking.Application.Analytics;

/// <summary>Granularity of the occupancy report (UC-04). Day, week and month are sums of hourly rows (ADR-0010).</summary>
public enum ReportGranularity
{
    Hour = 1,
    Day = 2,

    /// <summary>ISO week (Monday to Sunday), in the report time zone.</summary>
    Week = 3,
    Month = 4,
}

/// <summary>
/// Parameters of <c>GET /api/reports/occupancy?zone=&amp;from=&amp;to=&amp;granularity=</c> (UC-04, RF-05), validated here.
/// <c>from</c> is inclusive and <c>to</c> exclusive; both must be whole hours (every summary row is one UTC hour).
/// </summary>
public sealed record OccupancyReportRequest(string? Zone, DateTimeOffset From, DateTimeOffset To, ReportGranularity Granularity)
{
    /// <summary>
    /// Longest range per granularity, so that one request cannot ask for an unbounded number of buckets. Provisional
    /// values (an hourly report of one month, other granularities up to about 3 years). TODO(Diogo): confirm in Cap. 3.
    /// </summary>
    public static readonly TimeSpan MaxHourlyRange = TimeSpan.FromDays(31);

    public static readonly TimeSpan MaxRange = TimeSpan.FromDays(3 * 366);

    /// <summary>
    /// Builds and validates a request from the raw query values (null = not given). A date without an offset is UTC.
    /// <paramref name="granularity"/> defaults to <c>hour</c>.
    /// </summary>
    public static OccupancyReportRequest Create(string? zone, string? from, string? to, string? granularity)
    {
        if (zone is not null && (string.IsNullOrWhiteSpace(zone) || zone.Length > ParkingFacility.ZoneMaxLength))
        {
            throw new DomainValidationException("zone", $"must not be blank and must have at most {ParkingFacility.ZoneMaxLength} characters");
        }

        var start = ParseInstant(from, "from");
        var end = ParseInstant(to, "to");
        if (end <= start)
        {
            throw new DomainValidationException("to", "must be later than from");
        }

        // The four names only: Enum.TryParse would also accept "1" or "hour,day" (= week; review of Phase 6, finding 7).
        var unit = granularity?.ToLowerInvariant() switch
        {
            null or "hour" => ReportGranularity.Hour,
            "day" => ReportGranularity.Day,
            "week" => ReportGranularity.Week,
            "month" => ReportGranularity.Month,
            _ => throw new DomainValidationException("granularity", "must be one of hour, day, week, month"),
        };

        var max = unit == ReportGranularity.Hour ? MaxHourlyRange : MaxRange;
        if (end - start > max)
        {
            throw new DomainValidationException(
                "to", string.Create(CultureInfo.InvariantCulture, $"the range must be at most {max.TotalDays} days for granularity {unit.ToString().ToLowerInvariant()}"));
        }

        return new OccupancyReportRequest(zone?.Trim(), start, end, unit);
    }

    private static DateTimeOffset ParseInstant(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new DomainValidationException(field, "is required (ISO 8601, e.g. 2026-11-16T10:00:00Z)");
        }

        if (!IsoInstant.TryParse(value, out var instant))
        {
            throw new DomainValidationException(
                field, "is not an ISO 8601 date and time (e.g. 2026-11-16T10:00:00Z; write a + offset as %2B in the URL)");
        }

        if (!OccupancyIntervalCalculator.IsWholeHour(instant))
        {
            throw new DomainValidationException(field, "must be a whole hour");
        }

        return instant.ToUniversalTime();
    }
}

/// <summary>
/// ISO 8601 instants only (review of Phase 6, finding 7: <see cref="DateTimeOffset.TryParse(string?, out DateTimeOffset)"/>
/// also accepts "11/16/2026"). Minutes are required; seconds, fractions and the offset are optional; without an offset
/// the instant is UTC. Shared by the report and the <c>aggregate-occupancy</c> command.
/// </summary>
public static class IsoInstant
{
    private static readonly string[] Formats =
    [
        "yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF",
        "yyyy-MM-dd'T'HH:mmK", "yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK",
    ];

    public static bool TryParse(string? value, out DateTimeOffset instant)
    {
        var ok = DateTimeOffset.TryParseExact(
            value, Formats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out instant);
        instant = instant.ToUniversalTime();
        return ok;
    }
}

/// <summary>Sums of the hourly rows of one zone in one period, as the summary store returns them.</summary>
/// <param name="PeriodStart">Start of the period in the report time zone, as a UTC instant.</param>
/// <param name="PeriodEnd">End of the period (exclusive), from the same time zone database as the start.</param>
/// <param name="Hours">Distinct hours with at least one summary row (to tell an empty period from a missing one).</param>
public sealed record OccupancyReportRow(
    string Zone, DateTimeOffset PeriodStart, DateTimeOffset PeriodEnd, int Hours, long Events, long OccupiedSeconds, long AvailableSeconds);

/// <summary>
/// Query side of UC-04 (RF-05): reads only the summary table, never the operational tables nor MongoDB (spec §4).
/// </summary>
public interface IOccupancyReportQueries
{
    /// <summary>Rows grouped by zone and period (<paramref name="timeZone"/> is an IANA name), ordered by period, zone.</summary>
    Task<IReadOnlyList<OccupancyReportRow>> GetAsync(OccupancyReportRequest request, string timeZone, CancellationToken cancellationToken);
}

/// <summary>Settings of the reports (section <c>Reports</c>).</summary>
public sealed class ReportSettings
{
    public const string SectionName = "Reports";

    /// <summary>
    /// IANA time zone of the calendar periods (day, week, month). Default Europe/Lisbon (ADR-0010): the city's day, with
    /// 23 or 25 hours on the clock-change days. Its offsets are whole hours, so every local period is a set of UTC hours.
    /// </summary>
    public string TimeZone { get; set; } = "Europe/Lisbon";

    /// <summary>Resolves <see cref="TimeZone"/>; throws at startup if the name is unknown.</summary>
    public TimeZoneInfo Resolve()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(TimeZone);
        if (zone.BaseUtcOffset.Ticks % TimeSpan.TicksPerHour != 0)
        {
            throw new InvalidOperationException(
                $"{SectionName}:TimeZone '{TimeZone}' has an offset that is not whole hours; periods would split UTC hours.");
        }

        return zone;
    }
}

/// <summary>One period of one zone in the report.</summary>
/// <param name="Hours">Hours of the period with summary rows.</param>
/// <param name="ExpectedHours">Hours of the period inside [from, to) (23 or 25 on clock-change days).</param>
/// <param name="OccupancyRate">Occupied ÷ available spot-minutes (ADR-0010); null when there are no spots.</param>
public sealed record OccupancyReportBucket(
    string Zone,
    DateTimeOffset Start,
    DateTimeOffset End,
    int Hours,
    int ExpectedHours,
    long Events,
    double OccupiedMinutes,
    double AvailableMinutes,
    double? OccupancyRate);

public sealed record OccupancyReport(
    string? Zone,
    ReportGranularity Granularity,
    string TimeZone,
    DateTimeOffset From,
    DateTimeOffset To,
    IReadOnlyList<OccupancyReportBucket> Buckets);

/// <summary>
/// UC-04 query handler: reads the summary rows and turns them into periods with a rate. Start and end of each period
/// come from the store (PostgreSQL's time zone database), never from <see cref="TimeZoneInfo"/>, so the two always agree
/// (review of Phase 6, finding 6).
/// </summary>
public sealed class OccupancyReportService(IOccupancyReportQueries queries, ReportSettings settings)
{
    public async Task<OccupancyReport> GetAsync(OccupancyReportRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var rows = await queries.GetAsync(request, settings.TimeZone, cancellationToken).ConfigureAwait(false);
        var buckets = rows.Select(row =>
            {
                var start = row.PeriodStart.ToUniversalTime();
                var end = row.PeriodEnd.ToUniversalTime();
                var clippedStart = start > request.From ? start : request.From;
                var clippedEnd = end < request.To ? end : request.To;
                return new OccupancyReportBucket(
                    row.Zone,
                    start,
                    end,
                    row.Hours,
                    (int)Math.Round((clippedEnd - clippedStart).TotalHours),
                    row.Events,
                    Math.Round(row.OccupiedSeconds / 60d, 2),
                    Math.Round(row.AvailableSeconds / 60d, 2),
                    row.AvailableSeconds > 0 ? Math.Round((double)row.OccupiedSeconds / row.AvailableSeconds, 4) : null);
            })
            .ToList();
        return new OccupancyReport(request.Zone, request.Granularity, settings.TimeZone, request.From, request.To, buckets);
    }
}
