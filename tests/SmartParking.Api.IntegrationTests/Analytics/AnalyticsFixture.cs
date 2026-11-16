using System.Diagnostics.Metrics;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartParking.Api.IntegrationTests.Relational;
using SmartParking.Application.Abstractions;
using SmartParking.Application.Analytics;
using SmartParking.Application.Configuration;
using SmartParking.Domain.Facilities;
using SmartParking.Domain.Occupancy;
using SmartParking.Infrastructure.Relational;
using SmartParking.Infrastructure.Relational.Analytics;

namespace SmartParking.Api.IntegrationTests.Analytics;

/// <summary>
/// Phase 6 (UC-04, RF-05): the API with PostgreSQL (migrated), MongoDB and Redis containers, plus helpers to write
/// synthetic events straight into the MongoDB log and to run the aggregation like SmartParking.Workers does. Every test
/// creates its own facilities in its own zone, so the reports of one test never see another test's rows.
/// </summary>
public sealed class AnalyticsFixture : IAsyncLifetime
{
    private readonly ApiFixture _api = new();

    public ApiFixture Api => _api;

    public HttpClient Client { get; private set; } = null!;

    public IServiceProvider Services => _api.Factory.Services;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>One metrics instance for every aggregator the tests build (one gauge, as in the Workers).</summary>
    public AnalyticsMetrics Metrics => field ??= new AnalyticsMetrics(Services.GetRequiredService<IMeterFactory>(), TimeProvider.System);

    public async ValueTask InitializeAsync()
    {
        await _api.InitializeAsync();
        Client = _api.Factory.CreateClient();
        await using var scope = Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<SmartParkingDbContext>().Database.MigrateAsync(Ct);
    }

    public async ValueTask DisposeAsync()
    {
        Client?.Dispose();
        await _api.DisposeAsync();
    }

    public static string NewZone() => $"zone-{Guid.NewGuid():N}";

    /// <summary>A facility with <paramref name="spots"/> spots in <paramref name="zone"/>, created through the API (RF-06).</summary>
    public async Task<(Guid Facility, Guid[] Spots)> CreateFacilityAsync(string zone, int spots)
    {
        var response = await Client.PostAsJsonAsync(
            "/api/facilities",
            new CreateFacility("Analytics", new GeoJsonPoint("Point", [-8.4103, 40.2033]), spots, FacilityType.Uncovered, "CMC", zone),
            RelationalFixture.Json,
            Ct);
        response.EnsureSuccessStatusCode();
        var facility = (await response.Content.ReadFromJsonAsync<FacilityDto>(RelationalFixture.Json, Ct))!;

        var ids = new Guid[spots];
        for (var i = 0; i < spots; i++)
        {
            var spot = await Client.PostAsJsonAsync(
                $"/api/facilities/{facility.Id}/spots", new CreateSpot($"A-{i}", VehicleType.Car), RelationalFixture.Json, Ct);
            spot.EnsureSuccessStatusCode();
            ids[i] = (await spot.Content.ReadFromJsonAsync<SpotDto>(RelationalFixture.Json, Ct))!.Id;
        }

        return (facility.Id, ids);
    }

    /// <summary>Appends synthetic events to the immutable log, as the log writer of UC-01 would.</summary>
    public Task AppendAsync(Guid facility, params (Guid Spot, OccupancyEventType Type, DateTimeOffset At)[] events) =>
        AppendWithIdsAsync(facility, events.Select(e => (Guid.NewGuid(), e.Spot, e.Type, e.At)).ToArray());

    /// <summary>Like <see cref="AppendAsync"/>, with the event ids chosen by the test (ties on the timestamp).</summary>
    public async Task AppendWithIdsAsync(Guid facility, params (Guid EventId, Guid Spot, OccupancyEventType Type, DateTimeOffset At)[] events)
    {
        var log = Services.GetRequiredService<IOccupancyEventLog>();
        var records = events
            .Select(e => new OccupancyEventRecord(e.EventId, Guid.NewGuid(), e.Spot, facility, e.Type, e.At, "{}", e.At))
            .ToList();
        var result = await log.AppendAsync(records, Ct);
        Assert.Equal(records.Count, result.Inserted);
    }

    /// <summary>An aggregator wired like the Workers (PostgreSQL configuration + MongoDB log → summary table).</summary>
    public OccupancyAggregator Aggregator(IServiceProvider scoped, TimeProvider? time = null, AnalyticsOptions? options = null)
    {
        time ??= TimeProvider.System;
        return ActivatorUtilities.CreateInstance<OccupancyAggregator>(scoped, options ?? new AnalyticsOptions(), Metrics, time);
    }

    public async Task<AggregationReport> AggregateAsync(DateTimeOffset from, DateTimeOffset to)
    {
        await using var scope = Services.CreateAsyncScope();
        return await Aggregator(scope.ServiceProvider).AggregateAsync(from, to, Ct);
    }

    /// <summary>The summary rows of one facility, oldest hour first.</summary>
    public async Task<List<OccupancyHourlyRow>> RowsAsync(Guid facility)
    {
        await using var scope = Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<SmartParkingDbContext>();
        return await context.OccupancyHourly.AsNoTracking()
            .Where(r => r.FacilityId == facility)
            .OrderBy(r => r.HourStart)
            .ToListAsync(Ct);
    }

    public async Task<OccupancyReport> ReportAsync(string zone, string from, string to, string granularity)
    {
        var response = await Client.GetAsync(
            $"/api/reports/occupancy?zone={Uri.EscapeDataString(zone)}&from={Uri.EscapeDataString(from)}&to={Uri.EscapeDataString(to)}&granularity={granularity}",
            Ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<OccupancyReport>(RelationalFixture.Json, Ct))!;
    }
}

/// <summary>A clock the test moves by hand.</summary>
public sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

[CollectionDefinition(Name)]
public sealed class AnalyticsTestGroup : ICollectionFixture<AnalyticsFixture>
{
    public const string Name = "Analytics (PostgreSQL + MongoDB)";
}
