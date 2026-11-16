using DotNet.Testcontainers.Containers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Testcontainers.MongoDb;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;
using SmartParking.Infrastructure.Relational.Monolithic;
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
        "postgis/postgis:16-3.5-alpine@sha256:47e961a569fd52ff31f0fe205ed91eeab17d9f5fff6722e6d7ea6b588748b293";
    public const string MongoImage =
        "mongo:7.0.48@sha256:9896df581dfd0718b7abe112f4c37598d0986c54060e4ec1384997e6503c8c71";
    public const string RedisImage =
        "redis:7.4.12-alpine@sha256:7f7d30b7fdd09467bac58198a1f166dd69789d348d4fe859cccb20e0cf287fbe";

    /// <summary>As in docker-compose.yml, an empty tmpfs hides the image's init script (PostGIS goes to its own schema, ADR-0012).</summary>
    public static PostgreSqlContainer NewPostgres() =>
        new PostgreSqlBuilder(PostgresImage).WithTmpfsMount("/docker-entrypoint-initdb.d").Build();

    /// <summary>Variant of the API under test: <see cref="TestMode.Current"/> unless a test asks for a specific one.</summary>
    public PersistenceMode Mode { get; }

    public ApiFixture()
        : this(TestMode.Current)
    {
    }

    // Internal: xUnit accepts a class fixture with a single public constructor.
    internal ApiFixture(PersistenceMode mode) => Mode = mode;

    public bool IsMonolithic => Mode == PersistenceMode.Monolithic;

    public PostgreSqlContainer Postgres { get; } = NewPostgres();

    public MongoDbContainer Mongo { get; } = new MongoDbBuilder(MongoImage).Build();

    public RedisContainer Redis { get; } = new RedisBuilder(RedisImage).Build();

    public WebApplicationFactory<Program> Factory { get; private set; } = null!;

    /// <summary>Container behind each readiness check name.</summary>
    public IContainer ContainerFor(string healthCheckName) => healthCheckName switch
    {
        RelationalRegistration.HealthCheckName => Postgres,
        DocumentsRegistration.HealthCheckName when !IsMonolithic => Mongo,
        CacheRegistration.HealthCheckName when !IsMonolithic => Redis,
        _ => throw new ArgumentOutOfRangeException(nameof(healthCheckName), healthCheckName, null),
    };

    /// <summary>Readiness checks of the variant under test, in name order.</summary>
    public static IReadOnlyList<string> ReadinessChecks(PersistenceMode mode) => mode == PersistenceMode.Monolithic
        ? [RelationalRegistration.HealthCheckName]
        : [DocumentsRegistration.HealthCheckName, RelationalRegistration.HealthCheckName, CacheRegistration.HealthCheckName];

    /// <summary>
    /// Starts the containers. The API itself starts lazily, on the first <c>Factory.CreateClient()</c>,
    /// so a test can stop a container before the API boots. In the monolithic variant MongoDB and Redis are not started
    /// and their connection strings are not given: if anything of the hybrid sub-layers were still registered, the API
    /// would fail at startup ("Missing connection string") instead of silently using them (Phase 8 plan, point 3).
    /// </summary>
    public async ValueTask InitializeAsync()
    {
        if (IsMonolithic)
        {
            await Postgres.StartAsync();
        }
        else
        {
            await Task.WhenAll(Postgres.StartAsync(), Mongo.StartAsync(), Redis.StartAsync());
        }

        // Resolve now: the builder delegate runs when the API boots, and a stopped container
        // no longer has a mapped port.
        var postgres = Postgres.GetConnectionString();
        var mongo = IsMonolithic ? null : Mongo.GetConnectionString();
        var redis = IsMonolithic ? null : Redis.GetConnectionString();

        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(EnvironmentName);
            builder.UseSetting(PersistenceModeConfiguration.Key, Mode.ToString());
            builder.UseSetting($"ConnectionStrings:{RelationalRegistration.ConnectionStringName}", postgres);
            if (!IsMonolithic)
            {
                builder.UseSetting($"ConnectionStrings:{DocumentsRegistration.ConnectionStringName}", mongo);
                builder.UseSetting($"ConnectionStrings:{CacheRegistration.ConnectionStringName}", redis);
            }
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
