using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using SmartParking.Application.Abstractions.Availability;
using SmartParking.Application.Abstractions.Reading;
using SmartParking.Domain.Facilities;
using SmartParking.Domain.Occupancy;
using SmartParking.Infrastructure;
using StackExchange.Redis;
using static SmartParking.Api.Tests.Fixtures.IngestionApi;

namespace SmartParking.Api.Tests.Fixtures;

/// <summary>
/// The availability view for the tests (spec 004, F5a). Reads Redis with the test's own multiplexer, seeds one site
/// through the host's <see cref="IAvailabilityViewStore"/>, and builds an isolated environment for the rebuild tests:
/// its own PostgreSQL database, MongoDB database and Redis database, so the rebuild sees only the test's data.
/// </summary>
internal static class AvailabilityView
{
    public const string Prefix = "avail:";

    /// <summary>Redis database of the isolated environment; the shared hosts use database 0.</summary>
    public const int IsolatedRedisDatabase = 1;

    public const string Channel = "availability:changed";

    public static string Id(Guid id) => id.ToString("D", CultureInfo.InvariantCulture);

    public static Task<ConnectionMultiplexer> ConnectAsync(ApiFixture fixture) =>
        ConnectionMultiplexer.ConnectAsync(fixture.Databases.Redis.GetConnectionString());

    /// <summary>Every <c>avail:</c> key of the database with its value as sorted text, for exact comparisons.</summary>
    public static async Task<SortedDictionary<string, string>> SnapshotAsync(IConnectionMultiplexer redis, int database = 0)
    {
        var db = redis.GetDatabase(database);
        var server = redis.GetServer(redis.GetEndPoints()[0]);
        var snapshot = new SortedDictionary<string, string>(StringComparer.Ordinal);

        await foreach (var key in server.KeysAsync(database, Prefix + "*"))
        {
            var type = await db.KeyTypeAsync(key);
            snapshot[key!] = type switch
            {
                RedisType.Hash => Join((await db.HashGetAllAsync(key)).Select(entry => $"{entry.Name}={entry.Value}")),
                RedisType.Set => Join((await db.SetMembersAsync(key)).Select(member => member.ToString())),
                RedisType.SortedSet => Join((await db.SortedSetRangeByRankWithScoresAsync(key))
                    .Select(entry => $"{entry.Element}:{entry.Score.ToString("R", CultureInfo.InvariantCulture)}")),
                _ => throw new InvalidOperationException($"Unexpected type {type} of {key}."),
            };
        }

        return snapshot;
    }

    public static async Task<Dictionary<string, string>> HashAsync(IConnectionMultiplexer redis, string key, int database = 0) =>
        (await redis.GetDatabase(database).HashGetAllAsync(key)).ToDictionary(entry => entry.Name.ToString(), entry => entry.Value.ToString());

    public static async Task<string[]> MembersAsync(IConnectionMultiplexer redis, string key, int database = 0) =>
        [.. (await redis.GetDatabase(database).SetMembersAsync(key)).Select(member => member.ToString()).Order(StringComparer.Ordinal)];

    /// <summary>Writes the layout of one site (all spots <c>Standard</c>, one zone) into the view of the host.</summary>
    public static Task SeedAsync(IServiceProvider services, Site site, CancellationToken cancellationToken)
    {
        var layout = new AvailabilityLayout(
        [
            new FacilityLayout(
                site.Facility.Id,
                site.Facility.Name,
                site.Facility.OperatorId,
                site.Facility.Latitude,
                site.Facility.Longitude,
                [new ZoneLayout(site.Zone.Id, site.Zone.Name)],
                [.. site.Spots.Select(spot => new SpotLayout(spot.SpotId, site.Zone.Id, SpotType.Standard))]),
        ]);

        return services.GetRequiredService<IAvailabilityViewStore>().SeedAsync(layout, cancellationToken);
    }

    /// <summary>An occupancy event of this site's spot, as the projector receives it.</summary>
    public static OccupancyEvent Occupancy(
        Site site, SpotWithSensor spot, OccupancyEventType type, DateTimeOffset timestamp, Guid? eventId = null) =>
        new(eventId ?? Guid.NewGuid(), spot.SensorId, spot.HardwareId, spot.SpotId, site.Facility.Id, site.Zone.Id, type, timestamp, timestamp, null);

    /// <summary>New ids in ascending order of the lowercase UUID text (the projector's tie-break).</summary>
    public static Guid[] OrderedIds(int count) =>
        [.. Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).OrderBy(Id, StringComparer.Ordinal)];

    /// <summary>
    /// Settings of an isolated environment: a new PostgreSQL database, a new MongoDB database and the Redis database
    /// <see cref="IsolatedRedisDatabase"/>. <see cref="Isolated.Rebuilder"/> uses the Workers login (spec 004, ronda 6).
    /// </summary>
    public static async Task<Isolated> CreateIsolatedAsync(ApiFixture fixture, CancellationToken cancellationToken)
    {
        var database = await fixture.Databases.CreateIsolatedDatabaseAsync(cancellationToken);
        var api = fixture.Databases.Settings(PersistenceMode.Hybrid);
        api["ConnectionStrings:Postgres"] = database.AppConnectionString;
        api["ConnectionStrings:Redis"] = fixture.Databases.Redis.GetConnectionString() + $",defaultDatabase={IsolatedRedisDatabase}";
        api["Mongo:Database"] = $"avail_{Guid.NewGuid():N}";

        var rebuilder = new Dictionary<string, string?>(api) { ["ConnectionStrings:Postgres"] = database.WorkersConnectionString };
        return new Isolated(api, rebuilder, api["Mongo:Database"]!);
    }

    public sealed record Isolated(
        IReadOnlyDictionary<string, string?> Api,
        IReadOnlyDictionary<string, string?> Rebuilder,
        string MongoDatabase);

    private static string Join(IEnumerable<string> values) => string.Join("|", values.Order(StringComparer.Ordinal));
}
