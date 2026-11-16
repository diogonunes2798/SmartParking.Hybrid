using System.Net.Http.Json;
using SmartParking.Api.IntegrationTests.Ingestion;
using SmartParking.Api.IntegrationTests.Relational;
using SmartParking.Application.Abstractions;
using SmartParking.Application.Availability;
using SmartParking.Domain.Facilities;
using SmartParking.Domain.Occupancy;
using SmartParking.Infrastructure.Cache.Availability;
using StackExchange.Redis;

namespace SmartParking.Api.IntegrationTests.Availability;

/// <summary>
/// RF-06 changes reach the read model through the outbox (ADR-0004/0008), the full UC-01 path ends in Redis, and the
/// read model can be rebuilt from scratch from PostgreSQL and the MongoDB log (ADR-0008, benchmarks of Phase 9).
/// </summary>
[Collection(AvailabilityTestGroup.Name)]
public sealed class ReadModelConfigurationAndRebuildTests(AvailabilityFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Facility_and_spot_changes_write_full_state_events_to_the_outbox()
    {
        var zone = $"zone-{Guid.NewGuid():N}";
        var facility = await fixture.CreateFacilityAsync("Parque da Portagem", 40.2089, -8.4292, zone);
        var spot = await fixture.CreateSpotAsync(facility.Id, VehicleType.Electric);

        var facilityRows = await fixture.OutboxAsync(facility.Id);
        var row = Assert.Single(facilityRows);
        Assert.Equal(nameof(FacilityChanged), row.Type);
        Assert.Contains("\"zone\":\"" + zone + "\"", row.Payload.Replace(" ", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);

        var spotRow = Assert.Single(await fixture.OutboxAsync(spot.Id));
        Assert.Equal(nameof(SpotChanged), spotRow.Type);
        Assert.Contains("Electric", spotRow.Payload, StringComparison.Ordinal);
    }

    /// <summary>
    /// New facility and spot (RF-06) → outbox → configuration topic (relay of Phase 5, simulated here) → projector:
    /// the facility appears in the GEO search with its spot free. Relaying the same rows again changes nothing.
    /// </summary>
    [Fact]
    public async Task New_facility_and_spot_reach_the_read_model_through_the_outbox()
    {
        await using var workers = await fixture.StartWorkersAsync();
        var zone = $"zone-{Guid.NewGuid():N}";
        var facility = await fixture.CreateFacilityAsync("Parque ISEC", 40.1925, -8.4121, zone);
        var spot = await fixture.CreateSpotAsync(facility.Id);

        var rows = (await fixture.OutboxAsync(facility.Id)).Concat(await fixture.OutboxAsync(spot.Id)).ToList();
        await fixture.RelayAsync(rows);

        await IngestionFixture.EventuallyAsync(
            async () => (await fixture.FacilityCountersAsync(facility.Id)).GetValueOrDefault("free") == 1
                && await fixture.Db.HashGetAsync(ReadModelKeys.Facility(facility.Id), "zone") == zone,
            "facility and spot projected");

        // Its own consumer group on the configuration stream (review of Phase 4, finding 6).
        var groups = await fixture.Db.StreamGroupInfoAsync(ConfigurationEventMessage.Topic);
        Assert.Contains(groups, g => g.Name == AvailabilityConsumers.Configuration);

        await fixture.RelayAsync(rows); // at-least-once: the relay may publish twice
        await Task.Delay(300, Ct);
        Assert.Equal(1, (await fixture.FacilityCountersAsync(facility.Id))["spots"]);
        Assert.Equal(1, (await fixture.ZoneCountersAsync(zone))["free"]);

        var found = await fixture.Client.GetFromJsonAsync<List<Dictionary<string, object>>>(
            "/api/availability?lat=40.2033&lon=-8.4103&radius=2000", RelationalFixture.Json, Ct);
        Assert.Contains(found!, f => f["facilityId"].ToString() == facility.Id.ToString());
    }

    [Fact]
    public async Task Zone_change_moves_the_counters_and_a_late_older_change_is_ignored()
    {
        var ct = Ct;
        var (facility, spots, zone) = await fixture.SeedAsync(2);
        await fixture.ReadModel.ApplyAsync([new OccupancyChange(Guid.NewGuid(), spots[0], SpotState.Occupied, 1_763_200_000_000)], ct);
        var newZone = $"zone-{Guid.NewGuid():N}";
        var later = DateTimeOffset.UtcNow.AddSeconds(5);

        var moved = await fixture.ReadModel.UpsertFacilityAsync(
            new FacilitySnapshot(facility, "Seeded", 40.2033, -8.4103, 100, FacilityType.Covered, newZone), later, ct);
        Assert.Equal(ConfigurationOutcome.Applied, moved);
        Assert.Equal(0, (await fixture.ZoneCountersAsync(zone)).GetValueOrDefault("spots"));
        Assert.Equal(2, (await fixture.ZoneCountersAsync(newZone))["spots"]);
        Assert.Equal(1, (await fixture.ZoneCountersAsync(newZone))["free"]);

        var stale = await fixture.ReadModel.UpsertFacilityAsync(
            new FacilitySnapshot(facility, "Seeded", 40.2033, -8.4103, 100, FacilityType.Covered, zone), later.AddSeconds(-1), ct);
        Assert.Equal(ConfigurationOutcome.Stale, stale);
        Assert.Equal(newZone, (string?)await fixture.Db.HashGetAsync(ReadModelKeys.Facility(facility), "zone"));
    }

    [Fact]
    public async Task Spot_type_change_and_delete_keep_counters_consistent()
    {
        var ct = Ct;
        var (facility, spots, zone) = await fixture.SeedAsync(2);
        var at = DateTimeOffset.UtcNow.AddSeconds(5);

        await fixture.ReadModel.UpsertSpotAsync(new SpotSnapshot(spots[0], facility, VehicleType.Electric), at, ct);
        var counters = await fixture.FacilityCountersAsync(facility);
        Assert.Equal(1, counters["spots:Car"]);
        Assert.Equal(1, counters["spots:Electric"]);
        Assert.Equal(1, counters["free:Electric"]);

        Assert.Equal(ConfigurationOutcome.Applied, await fixture.ReadModel.DeleteSpotAsync(spots[0], at.AddSeconds(1), ct));
        Assert.Equal(ConfigurationOutcome.Unknown, await fixture.ReadModel.DeleteSpotAsync(spots[0], at.AddSeconds(1), ct));
        counters = await fixture.FacilityCountersAsync(facility);
        Assert.Equal(1, counters["spots"]);
        Assert.Equal(1, counters["free"]);
        Assert.Equal(0, counters["spots:Electric"]);
        Assert.Equal(1, (await fixture.ZoneCountersAsync(zone))["spots"]);
        Assert.False(await fixture.Db.SetContainsAsync(ReadModelKeys.Facility(facility).Append(":spots"), spots[0].ToString()));
    }

    /// <summary>UC-01 end to end: API → stream → availability group → Redis, with the message on pub/sub.</summary>
    [Fact]
    public async Task Ingested_event_updates_the_read_model_and_is_published()
    {
        var (facility, spots, _) = await fixture.SeedAsync(1);
        var received = new System.Collections.Concurrent.ConcurrentQueue<string>();
        (await fixture.Redis.GetSubscriber().SubscribeAsync(RedisChannel.Literal(ReadModelKeys.Channel)))
            .OnMessage(m => received.Enqueue(m.Message.ToString()));
        await using var workers = await fixture.StartWorkersAsync();

        var body = IngestionFixture.Event(timestamp: DateTimeOffset.UtcNow.ToString("O"));
        body["spotId"] = spots[0].ToString();
        body["facilityId"] = facility.ToString();
        var response = await fixture.Client.PostAsJsonAsync("/api/ingest/occupancy", new[] { body }, Ct);
        response.EnsureSuccessStatusCode();

        await IngestionFixture.EventuallyAsync(
            async () => (string?)await fixture.Db.HashGetAsync(ReadModelKeys.Spot(spots[0]), "state") == "OCCUPIED",
            "projector applied the event");
        Assert.Equal(0, (await fixture.FacilityCountersAsync(facility))["free"]);
        await IngestionFixture.EventuallyAsync(
            () => Task.FromResult(received.Any(m => m.Contains(spots[0].ToString(), StringComparison.Ordinal))),
            "pub/sub message");
    }

    /// <summary>
    /// Rebuild from scratch: clears Redis, loads facilities and spots from PostgreSQL and applies the last event of
    /// each spot from MongoDB. The result equals the state built incrementally, and the meta key marks it loaded.
    /// </summary>
    [Fact]
    public async Task Rebuild_from_postgres_and_mongo_reproduces_the_incremental_state()
    {
        var ct = Ct;
        var zone = $"zone-{Guid.NewGuid():N}";
        var facility = await fixture.CreateFacilityAsync("Parque HUC", 40.2207, -8.4136, zone);
        var spotA = await fixture.CreateSpotAsync(facility.Id);
        var spotB = await fixture.CreateSpotAsync(facility.Id);
        var spotC = await fixture.CreateSpotAsync(facility.Id, VehicleType.Motorcycle);

        // Events in the MongoDB log (as the log writer stores them): A occupied then freed, B occupied, C nothing.
        var t = new DateTimeOffset(2026, 11, 15, 9, 0, 0, TimeSpan.Zero);
        var log = fixture.Services.GetService(typeof(IOccupancyEventLog)) as IOccupancyEventLog;
        Assert.NotNull(log);
        OccupancyEventRecord Record(Guid spot, OccupancyEventType type, DateTimeOffset at) =>
            new(Guid.NewGuid(), Guid.NewGuid(), spot, facility.Id, type, at, "{}", at);
        List<OccupancyEventRecord> records =
        [
            Record(spotA.Id, OccupancyEventType.Occupied, t),
            Record(spotA.Id, OccupancyEventType.Freed, t.AddMinutes(5)),
            Record(spotB.Id, OccupancyEventType.Occupied, t.AddMinutes(1)),
        ];
        await log.AppendAsync(records, ct);

        // Incremental state: configuration through the outbox rows, events through the projector path.
        foreach (var id in new[] { facility.Id, spotA.Id, spotB.Id, spotC.Id })
        {
            foreach (var row in await fixture.OutboxAsync(id))
            {
                var m = new ConfigurationEventMessage(row.Id, row.Type, row.AggregateId, row.OccurredAt, System.Text.Json.JsonDocument.Parse(row.Payload).RootElement);
                await new ConfigurationProjector(fixture.ReadModel, ReadModelGate.Opened(), Metrics(), TimeProvider.System)
                    .HandleAsync([new DeliveredMessage("0-1", new BusMessage(row.Id.ToString(), m.ToJson()))], ct);
            }
        }

        await fixture.ReadModel.ApplyAsync(
            [.. records.Select(r => new OccupancyChange(
                r.EventId, r.SpotId, r.Type == OccupancyEventType.Occupied ? SpotState.Occupied : SpotState.Free,
                r.Timestamp.ToUnixTimeMilliseconds()))],
            ct);
        var incrementalFacility = await fixture.FacilityCountersAsync(facility.Id);
        var incrementalZone = await fixture.ZoneCountersAsync(zone);
        Assert.Equal(3, incrementalFacility["spots"]);
        Assert.Equal(2, incrementalFacility["free"]); // A freed, C never reported (FREE), B occupied

        await using (var session = await fixture.ReadModel.BeginRebuildAsync(ct))
        {
            await session.ClearAsync(ct);
        }

        Assert.Empty(await fixture.HashAsync(ReadModelKeys.Facility(facility.Id)));
        Assert.Null(await fixture.ReadModel.GetLoadedAtAsync(ct));

        var report = await fixture.RebuildAsync();

        Assert.True(report.Facilities >= 1 && report.Spots >= 3 && report.Events >= 2);
        Assert.Equal(incrementalFacility["spots"], (await fixture.FacilityCountersAsync(facility.Id))["spots"]);
        Assert.Equal(incrementalFacility["free"], (await fixture.FacilityCountersAsync(facility.Id))["free"]);
        Assert.Equal(incrementalFacility["free:Car"], (await fixture.FacilityCountersAsync(facility.Id))["free:Car"]);
        Assert.Equal(incrementalZone["free"], (await fixture.ZoneCountersAsync(zone))["free"]);
        Assert.Equal("OCCUPIED", (string?)await fixture.Db.HashGetAsync(ReadModelKeys.Spot(spotB.Id), "state"));
        Assert.Equal(
            records[2].Timestamp.ToUnixTimeMilliseconds(),
            (long)await fixture.Db.HashGetAsync(ReadModelKeys.Spot(spotB.Id), "ts"));
        Assert.NotNull(await fixture.ReadModel.GetLoadedAtAsync(ct));

        // After the rebuild, an event older than the last one in the log is still recognised as stale.
        var late = await fixture.ReadModel.ApplyAsync(
            [new OccupancyChange(Guid.NewGuid(), spotA.Id, SpotState.Occupied, t.AddMinutes(1).ToUnixTimeMilliseconds())], ct);
        Assert.Equal([ApplyOutcome.Stale], late);
    }

    private ReadModelMetrics Metrics() =>
        new((System.Diagnostics.Metrics.IMeterFactory)fixture.Services.GetService(typeof(System.Diagnostics.Metrics.IMeterFactory))!);
}
