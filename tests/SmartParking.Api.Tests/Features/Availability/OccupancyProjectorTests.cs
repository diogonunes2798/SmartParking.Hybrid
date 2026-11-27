using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SmartParking.Api.Tests.Fixtures;
using SmartParking.Application.Abstractions.Availability;
using SmartParking.Domain.Occupancy;
using StackExchange.Redis;
using static SmartParking.Api.Tests.Fixtures.AvailabilityView;
using static SmartParking.Api.Tests.Fixtures.IngestionApi;

namespace SmartParking.Api.Tests.Features.Availability;

/// <summary>The availability projector (spec 004, AC-1 to AC-8): one Lua script per event over the Redis view.</summary>
[Collection(ApiCollectionDefinition.Name)]
public sealed class OccupancyProjectorTests(ApiFixture fixture)
{
    private static readonly DateTimeOffset T = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ApplyAsync_OccupiedOnFreeSpot_AppliedAndCountersDropByOne_AC1()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        var (site, projector, redis) = await SeedSiteAsync(2, cancellationToken);
        await using var _ = redis;
        var occupied = Occupancy(site, site.Spots[0], OccupancyEventType.Occupied, T);

        // Act
        var outcome = await projector.ApplyAsync(occupied, publishChanges: false, cancellationToken);

        // Assert
        Assert.Equal(ProjectionOutcome.Applied, outcome);
        var spot = await HashAsync(redis, SpotKey(site.Spots[0].SpotId));
        Assert.Equal(("Occupied", T.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture), Id(occupied.EventId)),
            (spot["state"], spot["lastTimestamp"], spot["lastEventId"]));
        await AssertCountersAsync(redis, site, free: 1, total: 2);
    }

    [Fact]
    public async Task ApplyAsync_NewerFreedAfterOccupied_CountersBackToInitial_AC2()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        var (site, projector, redis) = await SeedSiteAsync(2, cancellationToken);
        await using var _ = redis;
        await projector.ApplyAsync(Occupancy(site, site.Spots[0], OccupancyEventType.Occupied, T), false, cancellationToken);

        // Act
        var outcome = await projector.ApplyAsync(
            Occupancy(site, site.Spots[0], OccupancyEventType.Freed, T.AddSeconds(30)), false, cancellationToken);

        // Assert
        Assert.Equal(ProjectionOutcome.Applied, outcome);
        Assert.Equal("Free", (await HashAsync(redis, SpotKey(site.Spots[0].SpotId)))["state"]);
        await AssertCountersAsync(redis, site, free: 2, total: 2);
    }

    [Fact]
    public async Task ApplyAsync_SameEventTwice_DuplicateAndViewUnchanged_AC3()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        var (site, projector, redis) = await SeedSiteAsync(2, cancellationToken);
        await using var _ = redis;
        var occupied = Occupancy(site, site.Spots[0], OccupancyEventType.Occupied, T);
        await projector.ApplyAsync(occupied, false, cancellationToken);
        var before = await SnapshotAsync(redis);

        // Act
        var outcome = await projector.ApplyAsync(occupied, false, cancellationToken);

        // Assert
        Assert.Equal(ProjectionOutcome.Duplicate, outcome);
        Assert.Equal(before, await SnapshotAsync(redis));
    }

    [Fact]
    public async Task ApplyAsync_OlderOrTiedWithSmallerId_StaleAndTiedWithGreaterIdApplied_AC4()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        var (site, projector, redis) = await SeedSiteAsync(2, cancellationToken);
        await using var _ = redis;
        var ids = OrderedIds(3);
        var reference = Occupancy(site, site.Spots[0], OccupancyEventType.Occupied, T, ids[1]);
        await projector.ApplyAsync(reference, false, cancellationToken);
        var before = await SnapshotAsync(redis);

        // Act
        var older = await projector.ApplyAsync(
            Occupancy(site, site.Spots[0], OccupancyEventType.Freed, T.AddSeconds(-1)), false, cancellationToken);
        var tiedSmaller = await projector.ApplyAsync(
            Occupancy(site, site.Spots[0], OccupancyEventType.Freed, T, ids[0]), false, cancellationToken);
        var unchanged = await SnapshotAsync(redis);
        var tiedGreater = await projector.ApplyAsync(
            Occupancy(site, site.Spots[0], OccupancyEventType.Freed, T, ids[2]), false, cancellationToken);

        // Assert
        Assert.Equal((ProjectionOutcome.Stale, ProjectionOutcome.Stale, ProjectionOutcome.Applied), (older, tiedSmaller, tiedGreater));
        Assert.Equal(before, unchanged);
        Assert.Equal(Id(ids[2]), (await HashAsync(redis, SpotKey(site.Spots[0].SpotId)))["lastEventId"]);
    }

    [Fact]
    public async Task ApplyAsync_NewerOccupiedOnOccupied_AppliedPairChangesCountersDoNot_AC5()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        var (site, projector, redis) = await SeedSiteAsync(2, cancellationToken);
        await using var _ = redis;
        await projector.ApplyAsync(Occupancy(site, site.Spots[0], OccupancyEventType.Occupied, T), false, cancellationToken);
        var newer = Occupancy(site, site.Spots[0], OccupancyEventType.Occupied, T.AddMinutes(1));

        // Act
        var outcome = await projector.ApplyAsync(newer, false, cancellationToken);

        // Assert
        Assert.Equal(ProjectionOutcome.Applied, outcome);
        var spot = await HashAsync(redis, SpotKey(site.Spots[0].SpotId));
        Assert.Equal(("Occupied", Id(newer.EventId)), (spot["state"], spot["lastEventId"]));
        await AssertCountersAsync(redis, site, free: 1, total: 2);
    }

    [Fact]
    public async Task ApplyAsync_SpotNotInView_UnknownSpotAndViewUnchanged_AC6()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        var (site, projector, redis) = await SeedSiteAsync(1, cancellationToken);
        await using var _ = redis;
        var outside = site.Spots[0] with { SpotId = Guid.NewGuid() };
        var before = await SnapshotAsync(redis);

        // Act
        var outcome = await projector.ApplyAsync(Occupancy(site, outside, OccupancyEventType.Occupied, T), false, cancellationToken);

        // Assert
        Assert.Equal(ProjectionOutcome.UnknownSpot, outcome);
        Assert.Equal(before, await SnapshotAsync(redis));
    }

    [Fact]
    public async Task ApplyAsync_TransitionAndNonTransitions_PublishesOnlyTransition_AC7()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        var (site, projector, redis) = await SeedSiteAsync(2, cancellationToken);
        await using var _ = redis;
        var spotId = Id(site.Spots[0].SpotId);
        var sentinelId = Id(site.Spots[1].SpotId);
        var messages = new List<JsonElement>();
        var sentinelSeen = new TaskCompletionSource();
        var queue = await redis.GetSubscriber().SubscribeAsync(RedisChannel.Literal(Channel));
        queue.OnMessage(message =>
        {
            var json = JsonDocument.Parse(message.Message.ToString()).RootElement.Clone();
            var messageSpot = json.GetProperty("spotId").GetString();
            if (messageSpot == spotId)
            {
                lock (messages)
                {
                    messages.Add(json);
                }
            }
            else if (messageSpot == sentinelId)
            {
                sentinelSeen.TrySetResult();
            }
        });
        var transition = Occupancy(site, site.Spots[0], OccupancyEventType.Occupied, T);

        // Act
        await projector.ApplyAsync(transition, true, cancellationToken);
        await projector.ApplyAsync(transition, true, cancellationToken);
        await projector.ApplyAsync(Occupancy(site, site.Spots[0], OccupancyEventType.Freed, T.AddSeconds(-5)), true, cancellationToken);
        await projector.ApplyAsync(Occupancy(site, site.Spots[0], OccupancyEventType.Occupied, T.AddSeconds(5)), true, cancellationToken);
        // Messages of one publisher on one channel arrive in order: once the sentinel arrives, the others have too.
        await projector.ApplyAsync(Occupancy(site, site.Spots[1], OccupancyEventType.Occupied, T.AddSeconds(10)), true, cancellationToken);
        await sentinelSeen.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        await queue.UnsubscribeAsync();

        // Assert
        var message = Assert.Single(messages);
        Assert.Equal(spotId, message.GetProperty("spotId").GetString());
        Assert.Equal(Id(site.Facility.Id), message.GetProperty("facilityId").GetString());
        Assert.Equal(Id(site.Zone.Id), message.GetProperty("zoneId").GetString());
        Assert.Equal("Standard", message.GetProperty("spotType").GetString());
        Assert.Equal("Occupied", message.GetProperty("state").GetString());
        Assert.Equal(1, message.GetProperty("facilityFree").GetInt32());
        Assert.Equal(1, message.GetProperty("zoneFree").GetInt32());
        Assert.Equal(T, message.GetProperty("timestamp").GetDateTimeOffset());
        Assert.Equal(Id(transition.EventId), message.GetProperty("eventId").GetString());
    }

    [Fact]
    public async Task ApplyAsync_FiftyParallelOccupied_FreeDropsByFifty_AC8()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        var (site, projector, redis) = await SeedSiteAsync(50, cancellationToken);
        await using var _ = redis;

        // Act
        await Task.WhenAll(site.Spots.Select(spot =>
            projector.ApplyAsync(Occupancy(site, spot, OccupancyEventType.Occupied, T), false, cancellationToken)));

        // Assert
        await AssertCountersAsync(redis, site, free: 0, total: 50);
    }

    private static string SpotKey(Guid spotId) => $"avail:spot:{Id(spotId)}";

    private static async Task AssertCountersAsync(IConnectionMultiplexer redis, Site site, int free, int total)
    {
        foreach (var key in new[] { $"avail:facility:{Id(site.Facility.Id)}", $"avail:zone:{Id(site.Zone.Id)}" })
        {
            var hash = await HashAsync(redis, key);
            Assert.Equal(
                (free.ToString(System.Globalization.CultureInfo.InvariantCulture), free.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    total.ToString(System.Globalization.CultureInfo.InvariantCulture), total.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                (hash["free"], hash["free:Standard"], hash["total"], hash["total:Standard"]));
        }
    }

    private async Task<(Site Site, IOccupancyProjector Projector, ConnectionMultiplexer Redis)> SeedSiteAsync(
        int spots, CancellationToken cancellationToken)
    {
        using var client = fixture.Hybrid.CreateClient();
        var site = await CreateSiteAsync(client, spots, cancellationToken);
        await SeedAsync(fixture.Hybrid.Services, site, cancellationToken);
        return (site, fixture.Hybrid.Services.GetRequiredService<IOccupancyProjector>(), await ConnectAsync(fixture));
    }
}
