using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SmartParking.Api.IntegrationTests.Ingestion;
using SmartParking.Api.IntegrationTests.Relational;
using SmartParking.Application.Abstractions;
using SmartParking.Application.Availability;
using SmartParking.Application.Configuration;
using SmartParking.Application.Outbox;
using SmartParking.Domain.Facilities;
using SmartParking.Domain.Occupancy;
using SmartParking.Infrastructure.Relational;
using SmartParking.Infrastructure.Relational.Monolithic;
using SmartParking.Infrastructure.Relational.Outbox;

namespace SmartParking.Api.IntegrationTests.Monolithic;

/// <summary>
/// Phase 8 (spec §7, ADR-0012): the mechanisms of the monolithic variant itself. The functional tests of the other
/// groups prove the same HTTP contract in both modes; these prove that the variant is a fair baseline (right indexes,
/// partition pruning, strong consistency, no hybrid code running) and the UC-05 migration path (RNF-04 comparison).
/// </summary>
[Collection(MonolithicTestGroup.Name)]
public sealed class MonolithicVariantTests(MonolithicFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] HybridAssemblies =
    [
        "SmartParking.Infrastructure.Cache", "SmartParking.Infrastructure.Documents", "SmartParking.Infrastructure.Messaging",
        "StackExchange.Redis", "MongoDB.Driver", "MongoDB.Bson",
    ];

    [Fact]
    public async Task The_container_has_no_hybrid_service_and_readiness_checks_only_postgres()
    {
        var offending = fixture.Registrations
            .SelectMany(d => new[] { d.ServiceType, d.ImplementationType, d.ImplementationInstance?.GetType() })
            .Where(t => t is not null && HybridAssemblies.Contains(t.Assembly.GetName().Name))
            .Select(t => t!.FullName)
            .Distinct()
            .ToList();
        Assert.Empty(offending);

        Assert.IsType<PostgresEventBus>(fixture.Services.GetRequiredService<IEventBus>());
        Assert.IsType<MonolithicOccupancyStore>(fixture.Services.GetRequiredService<IOccupancyEventLog>());
        Assert.IsType<PostgresAvailabilityQueries>(fixture.Services.GetRequiredService<IAvailabilityQueries>());
        Assert.Null(fixture.Services.GetService<IAvailabilityReadModel>());

        var ready = await fixture.Client.GetFromJsonAsync<HealthResponse>("/health/ready", Ct);
        Assert.Equal(["postgres"], ready!.Checks.Select(c => c.Name));
    }

    [Fact]
    public async Task An_accepted_event_is_visible_to_the_availability_query_without_waiting()
    {
        var (facility, spots) = await CreateFacilityAsync(40.2033, -8.4103, 3);

        var response = await fixture.Client.PostAsJsonAsync(
            "/api/ingest/occupancy", new[] { Event(facility, spots[0], "OCCUPIED", DateTimeOffset.UtcNow.AddSeconds(-1)) },
            IngestionFixture.Json, Ct);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        // Strong consistency: the event and the spot state were committed before the 202 (no projector, no polling).
        var found = await SearchAsync(40.2033, -8.4103);
        var mine = Assert.Single(found, f => f.FacilityId == facility);
        Assert.Equal(3, mine.Spots);
        Assert.Equal(2, mine.Free);
    }

    [Fact]
    public async Task A_late_event_does_not_overwrite_a_newer_state_and_a_repeat_is_a_duplicate()
    {
        var (facility, spots) = await CreateFacilityAsync(40.1925, -8.4121, 1);
        var log = fixture.Services.GetRequiredService<IOccupancyEventLog>();
        var now = DateTimeOffset.UtcNow;
        var newer = Record(facility, spots[0], OccupancyEventType.Freed, now);
        var older = Record(facility, spots[0], OccupancyEventType.Occupied, now.AddMinutes(-1));

        Assert.Equal(1, (await log.AppendAsync([newer], Ct)).Inserted);
        Assert.Equal(1, (await log.AppendAsync([older], Ct)).Inserted); // stored in the log...
        var state = await fixture.ScalarAsync<short>(
            $"SELECT type FROM {MonolithicSchema.SpotState} WHERE spot_id = @spot", ("spot", spots[0]));
        Assert.Equal(MonolithicSchema.Freed, state); // ...but the state stays at the newest event

        var repeat = await log.AppendAsync([newer, newer], Ct);
        Assert.Equal(0, repeat.Inserted);
        Assert.Equal(2, repeat.Duplicates);
        Assert.Equal(1L, await fixture.ScalarAsync<long>(
            $"SELECT count(*) FROM {MonolithicSchema.OccupancyEvents} WHERE event_id = @id", ("id", newer.EventId)));
    }

    /// <summary>
    /// As the Redis projector (UnknownSpot), an event of a spot that is not registered is kept in the log but gives the spot
    /// no state: when the spot is created later, it starts free (review of Phase 8, architecture finding L2).
    /// </summary>
    [Fact]
    public async Task An_event_of_an_unknown_spot_is_logged_but_gives_it_no_state()
    {
        var log = fixture.Services.GetRequiredService<IOccupancyEventLog>();
        var unknown = Record(Guid.NewGuid(), Guid.NewGuid(), OccupancyEventType.Occupied, DateTimeOffset.UtcNow);

        Assert.Equal(1, (await log.AppendAsync([unknown], Ct)).Inserted);
        Assert.Equal(0L, await fixture.ScalarAsync<long>(
            $"SELECT count(*) FROM {MonolithicSchema.SpotState} WHERE spot_id = @spot", ("spot", unknown.SpotId)));
    }

    /// <summary>Counterpart of RedisUnreachableTests: with PostgreSQL down, ingestion answers 503 (the sender retries), not 500.</summary>
    [Fact]
    public async Task Ingestion_answers_503_when_postgres_is_down()
    {
        await using var api = new ApiFixture(PersistenceMode.Monolithic);
        await api.InitializeAsync();
        var client = api.Factory.CreateClient();
        await using (var scope = api.Factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<SmartParkingDbContext>().Database.MigrateAsync(Ct);
        }

        await api.Postgres.StopAsync(Ct);
        var response = await client.PostAsJsonAsync(
            "/api/ingest/occupancy", new[] { Event(Guid.NewGuid(), Guid.NewGuid(), "OCCUPIED", DateTimeOffset.UtcNow) },
            IngestionFixture.Json, Ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Events_land_in_their_monthly_partition_and_range_queries_are_pruned()
    {
        var log = fixture.Services.GetRequiredService<IOccupancyEventLog>();
        var facility = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var current = Record(facility, Guid.NewGuid(), OccupancyEventType.Occupied, now);
        var ancient = Record(facility, Guid.NewGuid(), OccupancyEventType.Occupied, new DateTimeOffset(2020, 1, 15, 0, 0, 0, TimeSpan.Zero));
        await log.AppendAsync([current, ancient], Ct);

        Assert.Equal($"monolith.occupancy_events_y{now:yyyy}m{now:MM}", await PartitionOfAsync(current.EventId));
        Assert.Equal(MonolithicSchema.DefaultPartition, await PartitionOfAsync(ancient.EventId));

        // A range inside the current month reads one partition only, through the (facility_id, "timestamp") index.
        var monthStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var plan = await ExplainAsync(
            MonolithicOccupancyStore.EventsSql,
            ("facility_id", facility), ("since", monthStart), ("until", monthStart.AddDays(1)));
        Assert.Contains($"occupancy_events_y{now:yyyy}m{now:MM}", plan, StringComparison.Ordinal);
        Assert.DoesNotContain("occupancy_events_default", plan, StringComparison.Ordinal);
        Assert.DoesNotContain($"occupancy_events_y{monthStart.AddMonths(1):yyyy}m{monthStart.AddMonths(1):MM}", plan, StringComparison.Ordinal);
        Assert.Contains("facility_id_timestamp", plan, StringComparison.Ordinal);
    }

    /// <summary>
    /// The recursive skip scan of the UC-04 initial state (review of Phase 8, migrations finding 1) gives exactly what the
    /// direct <c>DISTINCT ON</c> gives: the newest event of every spot before the instant, ties broken by the larger event id,
    /// across partitions (this month, last month and DEFAULT), for spots that are not registered too.
    /// </summary>
    [Fact]
    public async Task The_skip_scan_state_before_an_instant_equals_the_direct_distinct_on()
    {
        var log = fixture.Services.GetRequiredService<IOccupancyEventLog>();
        var facility = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var spots = Enumerable.Range(0, 6).Select(_ => Guid.NewGuid()).ToArray();
        var random = new Random(8);
        var records = new List<OccupancyEventRecord>();
        foreach (var spot in spots)
        {
            for (var i = 0; i < 12; i++)
            {
                var at = i switch
                {
                    0 => new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero), // DEFAULT partition
                    < 4 => now.AddDays(-35 - random.Next(5)),                    // last month
                    _ => now.AddMinutes(-random.Next(0, 600)),                   // this month, some ties
                };
                records.Add(Record(facility, spot, i % 2 == 0 ? OccupancyEventType.Occupied : OccupancyEventType.Freed, at));
            }
        }

        await log.AppendAsync(records, Ct);
        var other = Record(Guid.NewGuid(), spots[0], OccupancyEventType.Occupied, now.AddMinutes(-1)); // another facility
        await log.AppendAsync([other], Ct);

        foreach (var before in new[] { now.AddMinutes(-300), now.AddDays(-34), now.AddYears(-1), now.AddMinutes(1) })
        {
            var skipScan = await log.GetLatestPerSpotAsync(facility, before, Ct);
            var expected = records
                .Where(r => r.Timestamp < before)
                .GroupBy(r => r.SpotId)
                .Select(g => g.OrderByDescending(r => r.Timestamp.UtcTicks / 10).ThenByDescending(r => r.EventId.ToString()).First())
                .ToDictionary(r => r.SpotId, r => r.EventId);

            Assert.Equal(expected, skipScan.ToDictionary(e => e.SpotId, e => e.EventId));
        }
    }

    [Fact]
    public async Task Partition_maintenance_creates_the_months_ahead_and_is_idempotent()
    {
        var maintenance = fixture.Services.GetRequiredService<OccupancyPartitionMaintenance>();

        await maintenance.EnsureAsync(Ct); // the migration created up to 3 months ahead; this may add the fourth
        Assert.Equal(0, await maintenance.EnsureAsync(Ct));

        var ahead = DateTimeOffset.UtcNow.AddMonths(3);
        Assert.NotNull(await fixture.ScalarAsync<object>(
            "SELECT to_regclass(@name)::text", ("name", $"monolith.occupancy_events_y{ahead:yyyy}m{ahead:MM}")));
    }

    [Fact]
    public async Task The_availability_query_can_use_the_gist_index_on_the_facility_location()
    {
        await CreateFacilityAsync(40.2089, -8.4292, 1);
        var search = AvailabilitySearch.Create(40.2033, -8.4103, 2000, null, null);

        // With a handful of rows the planner rightly prefers a sequential scan; disabling it shows that the index is usable,
        // i.e. that the query repeats the indexed expression exactly (otherwise the plan would still be a Seq Scan).
        var connection = await fixture.DataSource.OpenConnectionAsync(Ct);
        await using var _ = connection;
        await using (var off = new NpgsqlCommand("SET enable_seqscan = off", connection))
        {
            await off.ExecuteNonQueryAsync(Ct);
        }

        await using var explain = new NpgsqlCommand("EXPLAIN " + PostgresAvailabilityQueries.SearchSql, connection);
        PostgresAvailabilityQueries.AddParameters(explain, search);
        var plan = await ReadPlanAsync(explain);
        Assert.Contains("ix_parking_facilities_geography", plan, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_outbox_relay_completes_its_rows_with_the_postgres_bus()
    {
        var (facility, _) = await CreateFacilityAsync(40.2228, -8.4357, 1);
        await using var scope = fixture.Services.CreateAsyncScope();
        var relay = ActivatorUtilities.CreateInstance<OutboxRelay>(
            scope.ServiceProvider, new OutboxMetrics(fixture.Services.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>()));

        while (await relay.RelayBatchAsync(100, Ct) > 0)
        {
        }

        var context = scope.ServiceProvider.GetRequiredService<SmartParkingDbContext>();
        var rows = await context.Set<OutboxMessage>().AsNoTracking().Where(m => m.AggregateId == facility).ToListAsync(Ct);
        Assert.NotEmpty(rows);
        Assert.All(rows, r => Assert.NotNull(r.ProcessedAt));
    }

    /// <summary>
    /// UC-05 in the monolithic variant: before <c>AddEnvironmentalColumns</c> the generation-2 readings have nowhere to go
    /// (explicit schema); after it, the same running store fills the columns without a restart. A separate database, so
    /// that migrating back and forth does not touch the fixture's.
    /// </summary>
    [Fact]
    public async Task Environmental_readings_are_stored_only_once_the_migration_has_added_their_columns()
    {
        var connectionString = new NpgsqlConnectionStringBuilder(fixture.PostgresConnectionString)
        {
            Database = $"uc05_{Guid.NewGuid():N}",
        }.ConnectionString;
        var options = new DbContextOptionsBuilder<SmartParkingDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable(DependencyInjection.MigrationsHistoryTable))
            .UseSnakeCaseNamingConvention()
            .Options;
        await using var db = new SmartParkingDbContext(options);
        try
        {
            await db.GetService<IMigrator>().MigrateAsync("AddMonolithicSchema", cancellationToken: Ct);
            await using var dataSource = NpgsqlDataSource.Create(connectionString);
            using var probe = new EnvironmentalColumnsProbe(
                dataSource, new MonolithicOptions { SchemaRefreshInterval = TimeSpan.FromMilliseconds(1) }, TimeProvider.System);
            var store = new MonolithicOccupancyStore(dataSource, probe, TimeProvider.System);
            const string gen2 = """{"sensorGeneration":2,"temperature":21.5,"airQuality":42,"noise":55.3}""";

            var before = Record(Guid.NewGuid(), Guid.NewGuid(), OccupancyEventType.Occupied, DateTimeOffset.UtcNow, gen2);
            Assert.Equal(1, (await store.AppendAsync([before], Ct)).Inserted); // accepted, readings dropped

            await db.GetService<IMigrator>().MigrateAsync("AddEnvironmentalColumns", cancellationToken: Ct);
            await Task.Delay(5, Ct);

            var after = Record(Guid.NewGuid(), Guid.NewGuid(), OccupancyEventType.Freed, DateTimeOffset.UtcNow, gen2);
            Assert.Equal(1, (await store.AppendAsync([after], Ct)).Inserted);

            var command = dataSource.CreateCommand(
                $"SELECT event_id, temperature, air_quality, noise FROM {MonolithicSchema.OccupancyEvents} ORDER BY \"timestamp\"");
            await using var _ = command;
            var rows = new Dictionary<Guid, (double? T, double? A, double? N)>();
            await using (var reader = await command.ExecuteReaderAsync(Ct))
            {
                while (await reader.ReadAsync(Ct))
                {
                    rows[reader.GetGuid(0)] = (Nullable(reader, 1), Nullable(reader, 2), Nullable(reader, 3));
                }
            }

            Assert.Equal((null, null, null), rows[before.EventId]); // existing rows are not rewritten
            Assert.Equal((21.5, 42.0, 55.3), rows[after.EventId]);
        }
        finally
        {
            await db.Database.EnsureDeletedAsync(Ct);
        }
    }

    [Fact]
    public void A_payload_reading_that_is_not_a_number_is_stored_as_null_and_a_non_object_payload_is_refused()
    {
        Assert.True(MonolithicOccupancyStore.TryReadPayload("""{"temperature":"hot","noise":40}""", true, out var values));
        Assert.Equal([null, null, 40.0], values);
        Assert.False(MonolithicOccupancyStore.TryReadPayload("[1,2]", true, out _));
    }

    // ---- helpers ----

    private static double? Nullable(NpgsqlDataReader reader, int i) => reader.IsDBNull(i) ? null : reader.GetDouble(i);

    private async Task<string?> PartitionOfAsync(Guid eventId) => await fixture.ScalarAsync<string>(
        $"SELECT tableoid::regclass::text FROM {MonolithicSchema.OccupancyEvents} WHERE event_id = @id", ("id", eventId));

    private async Task<string> ExplainAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = fixture.DataSource.CreateCommand("EXPLAIN " + sql);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value is DateTimeOffset d ? d.ToUniversalTime() : value);
        }

        return await ReadPlanAsync(command);
    }

    private static async Task<string> ReadPlanAsync(NpgsqlCommand command)
    {
        var lines = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            lines.Add(reader.GetString(0));
        }

        return string.Join('\n', lines);
    }

    private static OccupancyEventRecord Record(
        Guid facility, Guid spot, OccupancyEventType type, DateTimeOffset at, string payload = "{}") =>
        new(Guid.NewGuid(), Guid.NewGuid(), spot, facility, type, at, payload, at);

    private static Dictionary<string, object?> Event(Guid facility, Guid spot, string type, DateTimeOffset at) => new()
    {
        ["eventId"] = Guid.NewGuid().ToString(),
        ["sensorId"] = Guid.NewGuid().ToString(),
        ["spotId"] = spot.ToString(),
        ["facilityId"] = facility.ToString(),
        ["type"] = type,
        ["timestamp"] = at.ToString("O"),
        ["payload"] = new Dictionary<string, object?>(),
    };

    private async Task<(Guid Facility, Guid[] Spots)> CreateFacilityAsync(double latitude, double longitude, int spots)
    {
        var response = await fixture.Client.PostAsJsonAsync(
            "/api/facilities",
            new CreateFacility("Mono", new GeoJsonPoint("Point", [longitude, latitude]), 100, FacilityType.Uncovered, "CMC", $"zone-{Guid.NewGuid():N}"),
            RelationalFixture.Json,
            Ct);
        response.EnsureSuccessStatusCode();
        var facility = (await response.Content.ReadFromJsonAsync<FacilityDto>(RelationalFixture.Json, Ct))!;
        var ids = new Guid[spots];
        for (var i = 0; i < spots; i++)
        {
            var spot = await fixture.Client.PostAsJsonAsync(
                $"/api/facilities/{facility.Id}/spots", new CreateSpot($"M-{i}", VehicleType.Car), RelationalFixture.Json, Ct);
            spot.EnsureSuccessStatusCode();
            ids[i] = (await spot.Content.ReadFromJsonAsync<SpotDto>(RelationalFixture.Json, Ct))!.Id;
        }

        return (facility.Id, ids);
    }

    private async Task<List<Result>> SearchAsync(double latitude, double longitude) =>
        (await fixture.Client.GetFromJsonAsync<List<Result>>(
            FormattableString.Invariant($"/api/availability?lat={latitude}&lon={longitude}&radius=500"), RelationalFixture.Json, Ct))!;

    private sealed record Result(Guid FacilityId, int Spots, int Free);
}
