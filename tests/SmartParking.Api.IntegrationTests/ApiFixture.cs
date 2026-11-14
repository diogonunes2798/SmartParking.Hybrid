using DotNet.Testcontainers.Containers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Testcontainers.MongoDb;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;
using CacheRegistration = SmartParking.Infrastructure.Cache.DependencyInjection;
using DocumentsRegistration = SmartParking.Infrastructure.Documents.DependencyInjection;
using RelationalRegistration = SmartParking.Infrastructure.Relational.DependencyInjection;

namespace SmartParking.Api.IntegrationTests;

/// <summary>
/// Starts real PostgreSQL, MongoDB and Redis containers (same images as docker-compose.yml, checked by
/// <see cref="ComposeConsistencyTests"/>) and an in-memory instance of the API wired to them.
/// The API runs in the "Testing" environment, which has no connection strings of its own: if an override
/// below were not applied, startup would fail instead of silently using the docker compose databases.
/// Each instance owns its containers, so a test that stops one cannot affect other tests.
/// </summary>
public sealed class ApiFixture : IAsyncLifetime
{
    public const string EnvironmentName = "Testing";

    // Same tag@digest as docker-compose.yml.
    public const string PostgresImage =
        "postgres:16.16-alpine@sha256:0adbf25a9e96d38823bb813f3ac0ed41fadb665c88f1df7fe6dbfb060cbdc106";
    public const string MongoImage =
        "mongo:7.0.48@sha256:9896df581dfd0718b7abe112f4c37598d0986c54060e4ec1384997e6503c8c71";
    public const string RedisImage =
        "redis:7.4.12-alpine@sha256:7f7d30b7fdd09467bac58198a1f166dd69789d348d4fe859cccb20e0cf287fbe";

    public PostgreSqlContainer Postgres { get; } = new PostgreSqlBuilder(PostgresImage).Build();

    public MongoDbContainer Mongo { get; } = new MongoDbBuilder(MongoImage).Build();

    public RedisContainer Redis { get; } = new RedisBuilder(RedisImage).Build();

    public WebApplicationFactory<Program> Factory { get; private set; } = null!;

    /// <summary>Container behind each readiness check name.</summary>
    public IContainer ContainerFor(string healthCheckName) => healthCheckName switch
    {
        RelationalRegistration.HealthCheckName => Postgres,
        DocumentsRegistration.HealthCheckName => Mongo,
        CacheRegistration.HealthCheckName => Redis,
        _ => throw new ArgumentOutOfRangeException(nameof(healthCheckName), healthCheckName, null),
    };

    /// <summary>
    /// Starts the containers. The API itself starts lazily, on the first <c>Factory.CreateClient()</c>,
    /// so a test can stop a container before the API boots.
    /// </summary>
    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(Postgres.StartAsync(), Mongo.StartAsync(), Redis.StartAsync());

        // Resolve now: the builder delegate runs when the API boots, and a stopped container
        // no longer has a mapped port.
        var postgres = Postgres.GetConnectionString();
        var mongo = Mongo.GetConnectionString();
        var redis = Redis.GetConnectionString();

        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(EnvironmentName);
            builder.UseSetting($"ConnectionStrings:{RelationalRegistration.ConnectionStringName}", postgres);
            builder.UseSetting($"ConnectionStrings:{DocumentsRegistration.ConnectionStringName}", mongo);
            builder.UseSetting($"ConnectionStrings:{CacheRegistration.ConnectionStringName}", redis);
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (Factory is not null)
        {
            await Factory.DisposeAsync();
        }

        await Task.WhenAll(
            Postgres.DisposeAsync().AsTask(),
            Mongo.DisposeAsync().AsTask(),
            Redis.DisposeAsync().AsTask());
    }
}
