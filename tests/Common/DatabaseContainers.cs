using System.Security.Cryptography;
using Npgsql;
using SmartParking.Infrastructure;
using SmartParking.Infrastructure.Relational;
using Testcontainers.MongoDb;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace SmartParking.Tests.Common;

/// <summary>
/// PostgreSQL+PostGIS, MongoDB (replica set) and Redis, started once per test collection.
/// The schema is migrated as the owner (the container's superuser) and the hosts connect as
/// <see cref="AppLogin"/>, a member of <c>smartparking_app</c>, so the append-only audit is enforced (spec 001).
/// </summary>
public sealed class DatabaseContainers : IAsyncLifetime
{
    /// <summary>Login role of the Api, as created by <c>scripts/db-app-user.sh</c> in development.</summary>
    public const string AppLogin = "smartparking_api";

    /// <summary>Login role of the Workers, also a member of <c>smartparking_app</c> (spec 002, AC-29; plan 002b, point 1).</summary>
    public const string WorkersLogin = "smartparking_workers";

    // Hex only, so it can be written into CREATE ROLE without quoting issues; new for every run.
    private readonly string appPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    private readonly string workersPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    public PostgreSqlContainer Postgres { get; } = new PostgreSqlBuilder(ContainerImages.Postgres).Build();

    public MongoDbContainer Mongo { get; } = new MongoDbBuilder(ContainerImages.Mongo).WithReplicaSet().Build();

    public RedisContainer Redis { get; } = new RedisBuilder(ContainerImages.Redis).Build();

    /// <summary>Owner (superuser) of the test database: migrations and verification queries only.</summary>
    public string OwnerConnectionString => Postgres.GetConnectionString();

    /// <summary>The application login: what the hosts under test use.</summary>
    public string AppConnectionString => WithLogin(OwnerConnectionString, AppLogin, appPassword);

    /// <summary>The Workers login: what the Workers hosts under test use.</summary>
    public string WorkersConnectionString => WithLogin(OwnerConnectionString, WorkersLogin, workersPassword);

    public async ValueTask InitializeAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        await Task.WhenAll(Postgres.StartAsync(cancellationToken), Mongo.StartAsync(cancellationToken), Redis.StartAsync(cancellationToken));
        await RelationalDatabase.MigrateAsync(OwnerConnectionString, cancellationToken);
        await CreateAppLoginAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await Postgres.DisposeAsync();
        await Mongo.DisposeAsync();
        await Redis.DisposeAsync();
    }

    /// <summary>
    /// Host settings that point every sublayer at these containers; PostgreSQL through an application login
    /// (<see cref="AppLogin"/> unless <paramref name="login"/> is <see cref="WorkersLogin"/>).
    /// </summary>
    public Dictionary<string, string?> Settings(PersistenceMode mode, string login = AppLogin) => new()
    {
        [PersistenceOptions.ModeKey] = mode.ToString(),
        ["ConnectionStrings:Postgres"] = login == WorkersLogin ? WorkersConnectionString : AppConnectionString,
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

    /// <summary>
    /// A new database in the same container, migrated as the owner unless <paramref name="migrate"/> is false.
    /// For tests that need exact counts (AC-18) or a migration from scratch (AC-1). The application login works
    /// there too: roles are cluster-wide and the grants come with the migration.
    /// </summary>
    public async Task<IsolatedDatabase> CreateIsolatedDatabaseAsync(CancellationToken cancellationToken, bool migrate = true)
    {
        var name = $"sp_{Guid.NewGuid():N}"[..20];

        await using (var connection = new NpgsqlConnection(OwnerConnectionString))
        {
            await connection.OpenAsync(cancellationToken);

            // Identifiers cannot be parameters; the name is generated above (letters, digits, underscore).
            await using var create = new NpgsqlCommand($"CREATE DATABASE {name}", connection);
            await create.ExecuteNonQueryAsync(cancellationToken);
        }

        var owner = new NpgsqlConnectionStringBuilder(OwnerConnectionString) { Database = name }.ConnectionString;
        if (migrate)
        {
            await RelationalDatabase.MigrateAsync(owner, cancellationToken);
        }

        return new IsolatedDatabase(owner, WithLogin(owner, AppLogin, appPassword), WithLogin(owner, WorkersLogin, workersPassword));
    }

    private static string WithLogin(string connectionString, string username, string password) =>
        new NpgsqlConnectionStringBuilder(connectionString) { Username = username, Password = password }.ConnectionString;

    private async Task CreateAppLoginAsync(CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(OwnerConnectionString);
        await connection.OpenAsync(cancellationToken);

        // Role names and passwords cannot be parameters; both are constants or hex generated above.
        foreach (var (login, password) in new[] { (AppLogin, appPassword), (WorkersLogin, workersPassword) })
        {
            await using var command = new NpgsqlCommand(
                $"CREATE ROLE {login} LOGIN PASSWORD '{password}' IN ROLE {RelationalDatabase.ApplicationRole}",
                connection);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
