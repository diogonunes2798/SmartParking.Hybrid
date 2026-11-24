using SmartParking.Infrastructure;
using Testcontainers.MongoDb;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace SmartParking.Tests.Common;

/// <summary>PostgreSQL+PostGIS, MongoDB (replica set) and Redis, started once per test collection.</summary>
public sealed class DatabaseContainers : IAsyncLifetime
{
    public PostgreSqlContainer Postgres { get; } = new PostgreSqlBuilder(ContainerImages.Postgres).Build();

    public MongoDbContainer Mongo { get; } = new MongoDbBuilder(ContainerImages.Mongo).WithReplicaSet().Build();

    public RedisContainer Redis { get; } = new RedisBuilder(ContainerImages.Redis).Build();

    public async ValueTask InitializeAsync() =>
        await Task.WhenAll(Postgres.StartAsync(), Mongo.StartAsync(), Redis.StartAsync());

    public async ValueTask DisposeAsync()
    {
        await Postgres.DisposeAsync();
        await Mongo.DisposeAsync();
        await Redis.DisposeAsync();
    }

    /// <summary>Host settings that point every sublayer at these containers.</summary>
    public Dictionary<string, string?> Settings(PersistenceMode mode) => new()
    {
        [PersistenceOptions.ModeKey] = mode.ToString(),
        ["ConnectionStrings:Postgres"] = Postgres.GetConnectionString(),
        ["ConnectionStrings:Mongo"] = Mongo.GetConnectionString(),
        ["ConnectionStrings:Redis"] = Redis.GetConnectionString(),
        [HealthCheckTags.TimeoutKey] = "00:00:05",
    };

    /// <summary>Host settings where no database can be reached (closed port on the loopback).</summary>
    public static Dictionary<string, string?> UnreachableSettings(PersistenceMode mode) => new()
    {
        [PersistenceOptions.ModeKey] = mode.ToString(),
        ["ConnectionStrings:Postgres"] = "Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none;Timeout=2",
        ["ConnectionStrings:Mongo"] = "mongodb://127.0.0.1:1/?serverSelectionTimeoutMS=2000",
        ["ConnectionStrings:Redis"] = "127.0.0.1:1,connectTimeout=2000",
        [HealthCheckTags.TimeoutKey] = "00:00:03",
    };
}
