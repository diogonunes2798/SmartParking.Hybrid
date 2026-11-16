using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using SmartParking.Application.Analytics;
using SmartParking.Domain.Occupancy;

namespace SmartParking.Api.IntegrationTests.Analytics;

/// <summary>
/// UC-04 (RF-05) end to end with synthetic events of known occupancy: MongoDB log → aggregation → summary table →
/// <c>GET /api/reports/occupancy</c> (ADR-0010). Dates in November have Lisbon = UTC; the clock-change days are tested
/// on their own.
/// </summary>
[Collection(AnalyticsTestGroup.Name)]
public sealed class OccupancyReportTests(AnalyticsFixture fixture)
{
    private const OccupancyEventType Occupied = OccupancyEventType.Occupied;
    private const OccupancyEventType Freed = OccupancyEventType.Freed;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DateTimeOffset Utc(int year, int month, int day, int hour, int minute = 0) =>
        new(year, month, day, hour, minute, 0, TimeSpan.Zero);

    [Fact]
    public async Task One_spot_occupied_from_10_00_to_10_30_gives_50_percent_in_the_10_hour()
    {
        var zone = AnalyticsFixture.NewZone();
        var (facility, spots) = await fixture.CreateFacilityAsync(zone, 1);
        await fixture.AppendAsync(facility, (spots[0], Occupied, Utc(2026, 11, 10, 10)), (spots[0], Freed, Utc(2026, 11, 10, 10, 30)));

        await fixture.AggregateAsync(Utc(2026, 11, 10, 9), Utc(2026, 11, 10, 12));
        var report = await fixture.ReportAsync(zone, "2026-11-10T09:00:00Z", "2026-11-10T12:00:00Z", "hour");

        Assert.Equal(3, report.Buckets.Count);
        var ten = report.Buckets.Single(b => b.Start == Utc(2026, 11, 10, 10));
        Assert.Equal(0.5, ten.OccupancyRate);
        Assert.Equal(30, ten.OccupiedMinutes);
        Assert.Equal(60, ten.AvailableMinutes);
        Assert.Equal(2, ten.Events);
        Assert.Equal(Utc(2026, 11, 10, 11), ten.End);
        Assert.All(report.Buckets.Where(b => b != ten), b => Assert.Equal(0.0, b.OccupancyRate));
        Assert.Equal("Europe/Lisbon", report.TimeZone);
    }

    [Fact]
    public async Task Aggregating_the_same_window_twice_gives_the_same_rows()
    {
        var zone = AnalyticsFixture.NewZone();
        var (facility, spots) = await fixture.CreateFacilityAsync(zone, 3);
        await fixture.AppendAsync(
            facility,
            (spots[0], Occupied, Utc(2026, 11, 11, 7, 50)), // before the window: carried in
            (spots[0], Freed, Utc(2026, 11, 11, 8, 20)),
            (spots[1], Occupied, Utc(2026, 11, 11, 8, 45)),
            (spots[2], Occupied, Utc(2026, 11, 11, 9, 10)),
            (spots[2], Freed, Utc(2026, 11, 11, 9, 13)));
        var from = Utc(2026, 11, 11, 8);
        var to = Utc(2026, 11, 11, 10);

        var first = await fixture.AggregateAsync(from, to);
        var rowsOnce = await fixture.RowsAsync(facility);
        var second = await fixture.AggregateAsync(from, to);
        var rowsTwice = await fixture.RowsAsync(facility);

        Assert.Equal(first.Rows, second.Rows);
        Assert.Equal(2, rowsTwice.Count);
        Assert.Equal(
            rowsOnce.Select(r => (r.HourStart, r.Zone, r.SpotCount, r.OccupiedSeconds, r.AvailableSeconds, r.EventCount)),
            rowsTwice.Select(r => (r.HourStart, r.Zone, r.SpotCount, r.OccupiedSeconds, r.AvailableSeconds, r.EventCount)));

        // 8h: spot 0 from 8:00 to 8:20 (20 min) + spot 1 from 8:45 (15 min) = 35 min = 2100 s of 3 × 3600.
        // 9h: spot 1 all hour (60 min) + spot 2 for 3 min = 63 min = 3780 s.
        Assert.Equal([2100L, 3780L], rowsTwice.Select(r => r.OccupiedSeconds));
        Assert.Equal([10_800L, 10_800L], rowsTwice.Select(r => r.AvailableSeconds));
        Assert.Equal([2, 2], rowsTwice.Select(r => r.EventCount));
    }

    [Fact]
    public async Task A_range_across_days_sums_the_hours_of_each_day()
    {
        var zone = AnalyticsFixture.NewZone();
        var (facility, spots) = await fixture.CreateFacilityAsync(zone, 2);
        await fixture.AppendAsync(
            facility,
            (spots[0], Occupied, Utc(2026, 11, 12, 23, 30)), // 30 min on the 12th, 30 min on the 13th
            (spots[0], Freed, Utc(2026, 11, 13, 0, 30)),
            (spots[1], Occupied, Utc(2026, 11, 13, 1)),
            (spots[1], Freed, Utc(2026, 11, 13, 3)));

        await fixture.AggregateAsync(Utc(2026, 11, 12, 0), Utc(2026, 11, 14, 0));
        var days = await fixture.ReportAsync(zone, "2026-11-12T00:00:00Z", "2026-11-14T00:00:00Z", "day");
        var oneBucket = await fixture.ReportAsync(zone, "2026-11-12T22:00:00Z", "2026-11-13T04:00:00Z", "week");

        Assert.Equal(2, days.Buckets.Count);
        var twelfth = days.Buckets[0];
        var thirteenth = days.Buckets[1];
        Assert.Equal((Utc(2026, 11, 12, 0), Utc(2026, 11, 13, 0)), (twelfth.Start, twelfth.End));
        Assert.Equal((24, 24), (twelfth.Hours, twelfth.ExpectedHours));
        Assert.Equal(30, twelfth.OccupiedMinutes);
        Assert.Equal(2 * 24 * 60, twelfth.AvailableMinutes);
        Assert.Equal(Math.Round(30d / (2 * 24 * 60), 4), twelfth.OccupancyRate);
        Assert.Equal(30 + 120, thirteenth.OccupiedMinutes);
        Assert.Equal(3, thirteenth.Events);

        // A range that crosses midnight inside one week: 6 hours, the minutes of both days summed.
        var week = Assert.Single(oneBucket.Buckets);
        Assert.Equal(Utc(2026, 11, 9, 0), week.Start); // Monday
        Assert.Equal((6, 6), (week.Hours, week.ExpectedHours));
        Assert.Equal(30 + 30 + 120, week.OccupiedMinutes);
        Assert.Equal(Math.Round(180d / (2 * 6 * 60), 4), week.OccupancyRate);
    }

    [Theory]
    [InlineData("2026-03-29T00:00:00Z", "2026-03-29T23:00:00Z", 23)] // spring forward in Lisbon
    [InlineData("2026-10-24T23:00:00Z", "2026-10-26T00:00:00Z", 25)] // fall back in Lisbon
    public async Task Clock_change_days_have_23_or_25_hours_and_keep_an_exact_rate(string from, string to, int hours)
    {
        var zone = AnalyticsFixture.NewZone();
        var (facility, spots) = await fixture.CreateFacilityAsync(zone, 1);
        var start = DateTimeOffset.Parse(from, System.Globalization.CultureInfo.InvariantCulture);
        var end = DateTimeOffset.Parse(to, System.Globalization.CultureInfo.InvariantCulture);
        await fixture.AppendAsync(facility, (spots[0], Occupied, start)); // occupied the whole local day

        await fixture.AggregateAsync(start.AddHours(-2), end.AddHours(2));
        var report = await fixture.ReportAsync(zone, from, to, "day");

        var day = Assert.Single(report.Buckets);
        Assert.Equal((start, end), (day.Start, day.End));
        Assert.Equal((hours, hours), (day.Hours, day.ExpectedHours));
        Assert.Equal(hours * 60, day.AvailableMinutes);
        Assert.Equal(1.0, day.OccupancyRate);
    }

    [Fact]
    public async Task Without_a_zone_every_zone_is_reported_on_its_own()
    {
        var zoneA = AnalyticsFixture.NewZone();
        var zoneB = AnalyticsFixture.NewZone();
        var (a, spotsA) = await fixture.CreateFacilityAsync(zoneA, 1);
        var (b, spotsB) = await fixture.CreateFacilityAsync(zoneB, 1);
        await fixture.AppendAsync(a, (spotsA[0], Occupied, Utc(2026, 11, 14, 5)));
        await fixture.AppendAsync(b, (spotsB[0], Occupied, Utc(2026, 11, 14, 5, 45)));

        await fixture.AggregateAsync(Utc(2026, 11, 14, 5), Utc(2026, 11, 14, 6));
        var response = await fixture.Client.GetAsync("/api/reports/occupancy?from=2026-11-14T05:00Z&to=2026-11-14T06:00Z", Ct);
        var report = (await System.Net.Http.Json.HttpContentJsonExtensions.ReadFromJsonAsync<OccupancyReport>(
            response.Content, Relational.RelationalFixture.Json, Ct))!;

        Assert.Null(report.Zone);
        Assert.Equal(1.0, report.Buckets.Single(x => x.Zone == zoneA).OccupancyRate);
        Assert.Equal(0.25, report.Buckets.Single(x => x.Zone == zoneB).OccupancyRate);
    }

    [Fact]
    public async Task A_period_not_yet_aggregated_shows_fewer_hours_than_expected()
    {
        var zone = AnalyticsFixture.NewZone();
        await fixture.CreateFacilityAsync(zone, 1);

        await fixture.AggregateAsync(Utc(2026, 11, 15, 0), Utc(2026, 11, 15, 6));
        var report = await fixture.ReportAsync(zone, "2026-11-15T00:00:00Z", "2026-11-16T00:00:00Z", "day");

        var day = Assert.Single(report.Buckets);
        Assert.Equal((6, 24), (day.Hours, day.ExpectedHours));
    }

    [Theory]
    [InlineData("?to=2026-11-10T00:00Z", "from")]
    [InlineData("?from=2026-11-10T00:15Z&to=2026-11-11T00:00Z", "from")]
    [InlineData("?from=2026-11-10T00:00Z&to=2026-11-11T00:00Z&granularity=year", "granularity")]
    [InlineData("?from=2026-11-11T00:00Z&to=2026-11-10T00:00Z", "to")]
    public async Task Invalid_parameters_are_a_400_problem(string query, string field)
    {
        var response = await fixture.Client.GetAsync("/api/reports/occupancy" + query, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(field, await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    /// <summary>
    /// Spec §4 (UC-04): the report never reads the operational tables. Every SQL command EF Core executes against this
    /// fixture's PostgreSQL while the report runs is captured through EF Core's diagnostic events (the same
    /// <c>CommandExecuted</c> event that the manual test reads in the log). A second API host is not used for this: its
    /// «SmartParking» meter, once disposed, breaks the metrics assertions of the other collections (found in Phase 6, as in
    /// Phase 5).
    /// </summary>
    [Fact]
    public async Task The_report_reads_only_the_summary_tables()
    {
        var zone = AnalyticsFixture.NewZone();
        var (facility, spots) = await fixture.CreateFacilityAsync(zone, 1);
        await fixture.AppendAsync(facility, (spots[0], Occupied, Utc(2026, 11, 16, 1)));
        await fixture.AggregateAsync(Utc(2026, 11, 16, 1), Utc(2026, 11, 16, 2));

        var port = new NpgsqlConnectionStringBuilder(fixture.Api.Postgres.GetConnectionString()).Port;
        using var capture = CommandCapture.Start(port);

        foreach (var granularity in new[] { "hour", "day", "week", "month" })
        {
            var withZone = await fixture.Client.GetAsync(
                $"/api/reports/occupancy?zone={zone}&from=2026-11-16T00:00Z&to=2026-11-17T00:00Z&granularity={granularity}", Ct);
            withZone.EnsureSuccessStatusCode();
        }

        var allZones = await fixture.Client.GetAsync(
            "/api/reports/occupancy?from=2026-11-16T00:00Z&to=2026-11-17T00:00Z&granularity=day", Ct);
        allZones.EnsureSuccessStatusCode();

        var commands = capture.Commands;
        Assert.Equal(5, commands.Count);
        string[] operational =
        [
            "parking_facilities", "parking_spots", "sensors", "tariffs", "users", "vehicles", "parking_sessions", "payments",
            "audit_log", "outbox_messages", "aggregation_checkpoints",
        ];
        Assert.All(commands, sql =>
        {
            Assert.Contains("FROM analytics.occupancy_hourly", sql, StringComparison.Ordinal);
            Assert.DoesNotContain(operational, table => sql.Contains(table, StringComparison.Ordinal));
        });
    }

    /// <summary>Collects the SQL of EF Core's <c>CommandExecuted</c> events on the connections to one PostgreSQL port.</summary>
    private sealed class CommandCapture : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
    {
        private readonly ConcurrentQueue<string> _commands = new();
        private readonly List<IDisposable> _subscriptions = [];
        private readonly int _port;

        private CommandCapture(int port) => _port = port;

        public List<string> Commands => _commands.ToList();

        public static CommandCapture Start(int port)
        {
            var capture = new CommandCapture(port);
            lock (capture._subscriptions)
            {
                capture._subscriptions.Add(DiagnosticListener.AllListeners.Subscribe(capture));
            }

            return capture;
        }

        public void OnNext(DiagnosticListener value)
        {
            if (value.Name == DbLoggerCategory.Name)
            {
                lock (_subscriptions)
                {
                    _subscriptions.Add(value.Subscribe(this));
                }
            }
        }

        public void OnNext(KeyValuePair<string, object?> value)
        {
            if (value.Key == RelationalEventId.CommandExecuted.Name
                && value.Value is CommandExecutedEventData data
                && new NpgsqlConnectionStringBuilder(data.Command.Connection?.ConnectionString).Port == _port)
            {
                _commands.Enqueue(data.Command.CommandText);
            }
        }

        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void Dispose()
        {
            lock (_subscriptions)
            {
                _subscriptions.ForEach(s => s.Dispose());
                _subscriptions.Clear();
            }
        }
    }
}
