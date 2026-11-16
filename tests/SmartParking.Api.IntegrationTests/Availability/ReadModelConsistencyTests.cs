using System.Collections.Concurrent;
using System.Text.Json;
using SmartParking.Application.Availability;
using SmartParking.Domain.Facilities;
using SmartParking.Infrastructure.Cache.Availability;
using StackExchange.Redis;

namespace SmartParking.Api.IntegrationTests.Availability;

/// <summary>
/// Consistency of the read model under at-least-once, out-of-order delivery (ADR-0008): every change is one Lua
/// script, idempotent and ordered by (timestamp, eventId).
/// </summary>
[Collection(AvailabilityTestGroup.Name)]
public sealed class ReadModelConsistencyTests(AvailabilityFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly long T0 = new DateTimeOffset(2026, 11, 15, 10, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    [HybridFact]
    public async Task Occupied_then_freed_restores_the_initial_counts()
    {
        var (facility, spots, zone) = await fixture.SeedAsync(3);
        var initialFacility = await fixture.FacilityCountersAsync(facility);
        var initialZone = await fixture.ZoneCountersAsync(zone);
        Assert.Equal(3, initialFacility["free"]); // new spots start FREE (ADR-0008)
        Assert.Equal(3, initialFacility["free:Car"]);
        Assert.Equal(3, initialZone["free"]);

        var occupied = await fixture.ReadModel.ApplyAsync([Change(spots[0], SpotState.Occupied, T0)], Ct);
        Assert.Equal([ApplyOutcome.Applied], occupied);
        Assert.Equal(2, (await fixture.FacilityCountersAsync(facility))["free"]);
        Assert.Equal(2, (await fixture.ZoneCountersAsync(zone))["free:Car"]);

        var freed = await fixture.ReadModel.ApplyAsync([Change(spots[0], SpotState.Free, T0 + 1_000)], Ct);
        Assert.Equal([ApplyOutcome.Applied], freed);

        Assert.Equal(initialFacility, await fixture.FacilityCountersAsync(facility));
        Assert.Equal(initialZone, await fixture.ZoneCountersAsync(zone));
    }

    [HybridFact]
    public async Task Same_event_applied_twice_changes_nothing_and_publishes_once()
    {
        var (facility, spots, zone) = await fixture.SeedAsync(2);
        var messages = await SubscribeAsync();
        var change = Change(spots[1], SpotState.Occupied, T0);

        Assert.Equal([ApplyOutcome.Applied], await fixture.ReadModel.ApplyAsync([change], Ct));
        var facilityAfterFirst = await fixture.FacilityCountersAsync(facility);
        var zoneAfterFirst = await fixture.ZoneCountersAsync(zone);
        var spotAfterFirst = await fixture.Db.HashGetAllAsync(ReadModelKeys.Spot(spots[1]));

        // Redelivered twice: once alone, once in the same batch as itself.
        Assert.Equal([ApplyOutcome.Duplicate], await fixture.ReadModel.ApplyAsync([change], Ct));
        Assert.Equal([ApplyOutcome.Duplicate, ApplyOutcome.Duplicate], await fixture.ReadModel.ApplyAsync([change, change], Ct));

        Assert.Equal(facilityAfterFirst, await fixture.FacilityCountersAsync(facility));
        Assert.Equal(zoneAfterFirst, await fixture.ZoneCountersAsync(zone));
        Assert.Equivalent(spotAfterFirst, await fixture.Db.HashGetAllAsync(ReadModelKeys.Spot(spots[1])));
        Assert.Equal(1, facilityAfterFirst["free"]);

        await Task.Delay(200, Ct); // let any (wrong) extra message arrive
        var mine = messages.Where(m => m.Contains(spots[1].ToString(), StringComparison.Ordinal)).ToList();
        Assert.Single(mine);
    }

    [HybridFact]
    public async Task Older_event_arriving_late_is_ignored()
    {
        var (facility, spots, _) = await fixture.SeedAsync(1);

        // The sensor sent OCCUPIED (10:00) then FREED (10:05); the bus delivers FREED first.
        Assert.Equal([ApplyOutcome.Applied], await fixture.ReadModel.ApplyAsync([Change(spots[0], SpotState.Occupied, T0 + 300_000)], Ct));
        Assert.Equal([ApplyOutcome.Stale], await fixture.ReadModel.ApplyAsync([Change(spots[0], SpotState.Free, T0)], Ct));

        Assert.Equal("OCCUPIED", (string?)await fixture.Db.HashGetAsync(ReadModelKeys.Spot(spots[0]), "state"));
        Assert.Equal(0, (await fixture.FacilityCountersAsync(facility))["free"]);
    }

    [HybridFact]
    public async Task Same_state_reported_again_moves_only_the_timestamp()
    {
        var (facility, spots, _) = await fixture.SeedAsync(1);

        Assert.Equal([ApplyOutcome.Applied], await fixture.ReadModel.ApplyAsync([Change(spots[0], SpotState.Occupied, T0)], Ct));
        Assert.Equal([ApplyOutcome.Unchanged], await fixture.ReadModel.ApplyAsync([Change(spots[0], SpotState.Occupied, T0 + 1)], Ct));

        Assert.Equal(0, (await fixture.FacilityCountersAsync(facility))["free"]);
        Assert.Equal(T0 + 1, (long)await fixture.Db.HashGetAsync(ReadModelKeys.Spot(spots[0]), "ts"));
    }

    [HybridFact]
    public async Task Event_for_an_unknown_spot_changes_nothing()
    {
        var outcome = await fixture.ReadModel.ApplyAsync([Change(Guid.NewGuid(), SpotState.Occupied, T0)], Ct);
        Assert.Equal([ApplyOutcome.UnknownSpot], outcome);
    }

    /// <summary>
    /// Several projectors applying the same shuffled events concurrently (as with 2–4 consumers, RNF-02): whatever
    /// the interleaving, each spot ends in the state of its newest event and the counters equal the spot states.
    /// </summary>
    [HybridFact]
    public async Task Concurrent_out_of_order_application_keeps_counters_equal_to_spot_states()
    {
        var (facility, spots, zone) = await fixture.SeedAsync(20);
        var random = new Random(42); // reproducible
        var events = spots
            .SelectMany(spot => Enumerable.Range(0, 15).Select(i => Change(
                spot, random.Next(2) == 0 ? SpotState.Free : SpotState.Occupied, T0 + (i * 1_000) + random.Next(500))))
            .ToList();

        // Four "consumers", each receiving all events (at-least-once) in its own random order, in batches.
        var orders = Enumerable.Range(0, 4).Select(c => events.OrderBy(_ => random.Next()).ToList()).ToList();
        var consumers = orders.Select(async order =>
        {
            foreach (var batch in order.Chunk(25))
            {
                await fixture.ReadModel.ApplyAsync(batch, Ct);
            }
        });
        await Task.WhenAll(consumers);

        var expectedFree = 0;
        foreach (var spot in spots)
        {
            var newest = events.Where(e => e.SpotId == spot)
                .OrderBy(e => e.TimestampMs).ThenBy(e => e.EventId.ToString("D"), StringComparer.Ordinal).Last();
            var state = (string?)await fixture.Db.HashGetAsync(ReadModelKeys.Spot(spot), "state");
            Assert.Equal(newest.State == SpotState.Free ? "FREE" : "OCCUPIED", state);
            expectedFree += newest.State == SpotState.Free ? 1 : 0;
        }

        Assert.Equal(expectedFree, (await fixture.FacilityCountersAsync(facility))["free"]);
        Assert.Equal(expectedFree, (await fixture.FacilityCountersAsync(facility))["free:Car"]);
        Assert.Equal(expectedFree, (await fixture.ZoneCountersAsync(zone))["free"]);
    }

    [HybridFact]
    public async Task State_change_is_published_on_the_availability_channel()
    {
        var (facility, spots, _) = await fixture.SeedAsync(1);
        var messages = await SubscribeAsync();

        await fixture.ReadModel.ApplyAsync([Change(spots[0], SpotState.Occupied, T0)], Ct);

        await EventuallyAsync(() => messages.Any(m => m.Contains(spots[0].ToString(), StringComparison.Ordinal)));
        using var json = JsonDocument.Parse(messages.Single(m => m.Contains(spots[0].ToString(), StringComparison.Ordinal)));
        Assert.Equal(facility.ToString(), json.RootElement.GetProperty("facilityId").GetString());
        Assert.Equal("OCCUPIED", json.RootElement.GetProperty("state").GetString());
        Assert.Equal(0, json.RootElement.GetProperty("free").GetInt32());
    }

    private async Task<ConcurrentQueue<string>> SubscribeAsync()
    {
        var received = new ConcurrentQueue<string>();
        var channel = await fixture.Redis.GetSubscriber().SubscribeAsync(RedisChannel.Literal(ReadModelKeys.Channel));
        channel.OnMessage(m => received.Enqueue(m.Message.ToString()));
        return received;
    }

    private static async Task EventuallyAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the pub/sub message.");
            await Task.Delay(20, Ct);
        }
    }

    private static OccupancyChange Change(Guid spot, SpotState state, long timestampMs) =>
        new(Guid.NewGuid(), spot, state, timestampMs);
}
