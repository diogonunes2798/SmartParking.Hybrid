using System.CommandLine;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using SmartParking.Api.Features.Spots;
using SmartParking.Api.Tests.Fixtures;
using SmartParking.Application.Abstractions.Availability;
using SmartParking.Domain.Occupancy;
using SmartParking.ProjectionRebuilder;
using StackExchange.Redis;
using static SmartParking.Api.Tests.Fixtures.AvailabilityView;
using static SmartParking.Api.Tests.Fixtures.IngestionApi;

namespace SmartParking.Api.Tests.Features.Availability;

/// <summary>
/// The <c>ProjectionRebuilder</c> (spec 004, AC-9 to AC-11), run in process against an isolated environment: its own
/// PostgreSQL, MongoDB and Redis databases, seeded through an Api host on the same environment.
/// </summary>
[Collection(ApiCollectionDefinition.Name)]
public sealed class ProjectionRebuilderTests(ApiFixture fixture)
{
    private static readonly DateTimeOffset T = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Rebuild_ActiveAndInactiveConfiguration_OnlyActiveAllFree_AC9()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        var environment = await CreateIsolatedAsync(fixture, cancellationToken);
        await using var factory = ApiFixture.CreateFactory(environment.Api);
        using var client = factory.CreateClient();
        await using var redis = await ConnectAsync(fixture);

        var site = await CreateSiteAsync(client, 3, cancellationToken); // zone 1: three Standard spots
        var (zone2, _) = await ConfigurationApi.CreateZoneAsync(client, site.Facility.Id, cancellationToken);
        var accessible = await CreateSpotAsync(client, site.Facility.Id, zone2.Id, "Accessible", cancellationToken);
        var standard2 = await CreateSpotAsync(client, site.Facility.Id, zone2.Id, "Standard", cancellationToken);
        await UpdateSpotAsync(client, site.Facility.Id, site.Spots[2].SpotId, cancellationToken, status: "Inactive");
        var inactiveSite = await CreateSiteAsync(client, 1, cancellationToken);
        await DeactivateFacilityAsync(client, inactiveSite, cancellationToken);

        // Act
        var exitCode = await RunRebuilderAsync(environment, cancellationToken);

        // Assert
        Assert.Equal(ExitCodes.Success, exitCode);
        var facility = await HashAsync(redis, $"avail:facility:{Id(site.Facility.Id)}", IsolatedRedisDatabase);
        Assert.Equal(
            ("4", "4", "3", "3", "1", "1"),
            (facility["free"], facility["total"], facility["free:Standard"], facility["total:Standard"],
                facility["free:Accessible"], facility["total:Accessible"]));
        var zone1 = await HashAsync(redis, $"avail:zone:{Id(site.Zone.Id)}", IsolatedRedisDatabase);
        Assert.Equal(("2", "2", "2"), (zone1["free"], zone1["total"], zone1["free:Standard"]));
        var zone2Hash = await HashAsync(redis, $"avail:zone:{Id(zone2.Id)}", IsolatedRedisDatabase);
        Assert.Equal(("2", "2", "1", "1"), (zone2Hash["free"], zone2Hash["total"], zone2Hash["free:Accessible"], zone2Hash["free:Standard"]));

        foreach (var spotId in new[] { site.Spots[0].SpotId, site.Spots[1].SpotId, accessible, standard2 })
        {
            Assert.Equal("Free", (await HashAsync(redis, $"avail:spot:{Id(spotId)}", IsolatedRedisDatabase))["state"]);
        }

        var snapshot = await SnapshotAsync(redis, IsolatedRedisDatabase);
        Assert.DoesNotContain($"avail:spot:{Id(site.Spots[2].SpotId)}", snapshot.Keys);
        Assert.DoesNotContain($"avail:facility:{Id(inactiveSite.Facility.Id)}", snapshot.Keys);
        Assert.DoesNotContain($"avail:spot:{Id(inactiveSite.Spots[0].SpotId)}", snapshot.Keys);
        string[] expectedZones = [.. new[] { Id(site.Zone.Id), Id(zone2.Id) }.Order(StringComparer.Ordinal)];
        Assert.Equal(expectedZones, await MembersAsync(redis, $"avail:facility:{Id(site.Facility.Id)}:zones", IsolatedRedisDatabase));
        string[] expectedFacilities = [Id(site.Facility.Id)];
        Assert.Equal(
            expectedFacilities,
            await MembersAsync(redis, $"avail:operator:{Id(site.Facility.OperatorId)}:facilities", IsolatedRedisDatabase));
        var geo = await redis.GetDatabase(IsolatedRedisDatabase).SortedSetRangeByRankAsync("avail:geo:facilities");
        Assert.Equal(expectedFacilities, geo.Select(member => member.ToString()));
    }

    [Fact]
    public async Task Rebuild_AfterLiveProjectionWithRepeatsAndDisorder_RestoresSameKeysAndValues_AC10()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        var environment = await CreateIsolatedAsync(fixture, cancellationToken);
        await using var factory = ApiFixture.CreateFactory(environment.Api);
        using var client = factory.CreateClient();
        await using var redis = await ConnectAsync(fixture);
        var site = await CreateSiteAsync(client, 3, cancellationToken);
        Assert.Equal(ExitCodes.Success, await RunRebuilderAsync(environment, cancellationToken));

        // Spot 0: occupied, freed, a repeat and a late event. Spot 1: three events at the same instant, below the
        // millisecond (the log keeps milliseconds), applied greatest id first. Spot 2: no events.
        var ids = OrderedIds(3);
        var sameInstant = T.AddTicks(4_321);
        OccupancyEvent[] live =
        [
            Occupancy(site, site.Spots[0], OccupancyEventType.Occupied, T),
            Occupancy(site, site.Spots[0], OccupancyEventType.Freed, T.AddMinutes(2)),
            Occupancy(site, site.Spots[0], OccupancyEventType.Occupied, T.AddMinutes(1)),
            Occupancy(site, site.Spots[1], OccupancyEventType.Occupied, sameInstant, ids[2]),
            Occupancy(site, site.Spots[1], OccupancyEventType.Freed, sameInstant, ids[0]),
            Occupancy(site, site.Spots[1], OccupancyEventType.Freed, sameInstant.AddTicks(1), ids[1]),
        ];
        var projector = factory.Services.GetRequiredService<IOccupancyProjector>();
        foreach (var occupancyEvent in live.Append(live[1]).Append(live[3]))
        {
            await projector.ApplyAsync(occupancyEvent, publishChanges: false, cancellationToken);
        }

        await PostOkAsync(client, Batch([.. live.Select(ToJson)]), cancellationToken);
        var before = await SnapshotAsync(redis, IsolatedRedisDatabase);

        // Act
        var exitCode = await RunRebuilderAsync(environment, cancellationToken);

        // Assert
        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal(before, await SnapshotAsync(redis, IsolatedRedisDatabase));
        Assert.Equal(
            ("Free", "Occupied", Id(ids[2])),
            ((await HashAsync(redis, $"avail:spot:{Id(site.Spots[0].SpotId)}", IsolatedRedisDatabase))["state"],
                (await HashAsync(redis, $"avail:spot:{Id(site.Spots[1].SpotId)}", IsolatedRedisDatabase))["state"],
                (await HashAsync(redis, $"avail:spot:{Id(site.Spots[1].SpotId)}", IsolatedRedisDatabase))["lastEventId"]));
    }

    [Fact]
    public async Task Rebuild_KeyOutsidePrefix_KeyRemains_AC11()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        var environment = await CreateIsolatedAsync(fixture, cancellationToken);
        await using var redis = await ConnectAsync(fixture);
        var database = redis.GetDatabase(IsolatedRedisDatabase);
        var outside = $"other:{Guid.NewGuid():N}";
        await database.StringSetAsync(outside, "kept");
        await database.StringSetAsync($"avail:stale:{Guid.NewGuid():N}", "removed");

        try
        {
            // Act
            var exitCode = await RunRebuilderAsync(environment, cancellationToken);

            // Assert
            Assert.Equal(ExitCodes.Success, exitCode);
            Assert.Equal("kept", (string?)await database.StringGetAsync(outside));
            Assert.DoesNotContain((await SnapshotKeysAsync(redis)), key => key.StartsWith("avail:stale:", StringComparison.Ordinal));
        }
        finally
        {
            await database.KeyDeleteAsync(outside);
        }
    }

    [Fact]
    public async Task Rebuild_MonolithicMode_RefusesWithoutTouchingTheView()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        var environment = await CreateIsolatedAsync(fixture, cancellationToken);
        var settings = new Dictionary<string, string?>(environment.Rebuilder) { ["Persistence:Mode"] = "Monolithic" };

        // Act
        var exitCode = await RebuilderCommand.Create(settings).Parse([]).InvokeAsync(cancellationToken: cancellationToken);

        // Assert
        Assert.Equal(ExitCodes.NotHybrid, exitCode);
    }

    private static Task<int> RunRebuilderAsync(Isolated environment, CancellationToken cancellationToken) =>
        RebuilderCommand.Create(environment.Rebuilder).Parse([]).InvokeAsync(cancellationToken: cancellationToken);

    private static async Task<List<string>> SnapshotKeysAsync(IConnectionMultiplexer redis) =>
        [.. (await SnapshotAsync(redis, IsolatedRedisDatabase)).Keys];

    private static System.Text.Json.Nodes.JsonObject ToJson(OccupancyEvent occupancyEvent) => new()
    {
        ["eventId"] = occupancyEvent.EventId.ToString(),
        ["sensorId"] = occupancyEvent.HardwareId,
        ["spotId"] = occupancyEvent.SpotId.ToString(),
        ["type"] = occupancyEvent.Type.ToString(),
        ["timestamp"] = occupancyEvent.Timestamp.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
    };

    private static async Task<Guid> CreateSpotAsync(HttpClient client, Guid facilityId, Guid zoneId, string type, CancellationToken cancellationToken)
    {
        using var response = await client.PostAsJsonAsync(
            ConfigurationApi.Spots(facilityId), new CreateSpotRequest(zoneId, ConfigurationApi.NewCode(), type), cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<SpotResponse>(cancellationToken))!.Id;
    }
}
