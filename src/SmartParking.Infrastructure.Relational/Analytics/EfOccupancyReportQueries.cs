using Microsoft.EntityFrameworkCore;
using SmartParking.Application.Analytics;

namespace SmartParking.Infrastructure.Relational.Analytics;

/// <summary>
/// Query side of UC-04 (RF-05): one SQL query over <c>analytics.occupancy_hourly</c> and nothing else (spec §4: never the
/// operational tables, never MongoDB). Periods are cut in the database: the start with
/// <c>date_trunc(unit, hour_start, time zone)</c> and the end with <c>date_add(start, 1 unit, time zone)</c> (PostgreSQL 16),
/// so both come from the same time zone database (review of Phase 6, finding 6). They give local midnights and ISO weeks
/// (Monday) in the report time zone, as UTC instants (ADR-0010). The sums are summed again, never the rates averaged.
/// Two variants of the statement (with and without a zone) so that the planner sees a plain equality on <c>zone</c>.
/// </summary>
internal sealed class EfOccupancyReportQueries(SmartParkingDbContext context) : IOccupancyReportQueries
{
    public async Task<IReadOnlyList<OccupancyReportRow>> GetAsync(
        OccupancyReportRequest request, string timeZone, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var unit = request.Granularity switch
        {
            ReportGranularity.Hour => "hour",
            ReportGranularity.Day => "day",
            ReportGranularity.Week => "week",
            ReportGranularity.Month => "month",
            _ => throw new ArgumentOutOfRangeException(nameof(request), request.Granularity, null),
        };
        var step = "1 " + unit;
        var from = request.From.ToUniversalTime();
        var to = request.To.ToUniversalTime();

        var query = request.Zone is { } zone
            ? context.Database.SqlQuery<ReportRow>(
                $"""
                 SELECT zone, period_start, date_add(period_start, {step}::interval, {timeZone}) AS period_end,
                        hours, events, occupied_seconds, available_seconds
                 FROM (
                     SELECT zone, date_trunc({unit}, hour_start, {timeZone}) AS period_start,
                            count(DISTINCT hour_start)::int AS hours, sum(event_count)::bigint AS events,
                            sum(occupied_seconds)::bigint AS occupied_seconds, sum(available_seconds)::bigint AS available_seconds
                     FROM analytics.occupancy_hourly
                     WHERE zone = {zone} AND hour_start >= {from} AND hour_start < {to}
                     GROUP BY zone, 2
                 ) AS periods
                 ORDER BY period_start, zone
                 """)
            : context.Database.SqlQuery<ReportRow>(
                $"""
                 SELECT zone, period_start, date_add(period_start, {step}::interval, {timeZone}) AS period_end,
                        hours, events, occupied_seconds, available_seconds
                 FROM (
                     SELECT zone, date_trunc({unit}, hour_start, {timeZone}) AS period_start,
                            count(DISTINCT hour_start)::int AS hours, sum(event_count)::bigint AS events,
                            sum(occupied_seconds)::bigint AS occupied_seconds, sum(available_seconds)::bigint AS available_seconds
                     FROM analytics.occupancy_hourly
                     WHERE hour_start >= {from} AND hour_start < {to}
                     GROUP BY zone, 2
                 ) AS periods
                 ORDER BY period_start, zone
                 """);

        var rows = await query.ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows
            .Select(r => new OccupancyReportRow(
                r.Zone, r.PeriodStart.ToUniversalTime(), r.PeriodEnd.ToUniversalTime(), r.Hours, r.Events, r.OccupiedSeconds, r.AvailableSeconds))
            .ToList();
    }

    /// <summary>Shape of one result row (column names in snake_case, like the rest of the schema).</summary>
    internal sealed class ReportRow
    {
        public required string Zone { get; init; }

        public required DateTimeOffset PeriodStart { get; init; }

        public required DateTimeOffset PeriodEnd { get; init; }

        public required int Hours { get; init; }

        public required long Events { get; init; }

        public required long OccupiedSeconds { get; init; }

        public required long AvailableSeconds { get; init; }
    }
}
