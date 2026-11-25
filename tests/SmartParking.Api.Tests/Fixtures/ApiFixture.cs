using Microsoft.Extensions.DependencyInjection;
using SmartParking.Infrastructure;
using SmartParking.Tests.Common;

namespace SmartParking.Api.Tests.Fixtures;

/// <summary>
/// Shared by the "Api" collection: starts the three databases once and builds one
/// WebApplicationFactory per scenario (mode, environment, extra services).
/// </summary>
public sealed class ApiFixture : IAsyncLifetime
{
    private ConfiguredFactory<Program>? hybrid;

    public DatabaseContainers Databases { get; } = new();

    /// <summary>One Api host in Hybrid mode shared by the feature tests (spec 001), created on first use.</summary>
    public ConfiguredFactory<Program> Hybrid => hybrid ??= CreateFactory();

    public ValueTask InitializeAsync() => Databases.InitializeAsync();

    public async ValueTask DisposeAsync()
    {
        if (hybrid is not null)
        {
            await hybrid.DisposeAsync();
        }

        await Databases.DisposeAsync();
    }

    public ConfiguredFactory<Program> CreateFactory(
        PersistenceMode mode = PersistenceMode.Hybrid,
        string environment = "Testing",
        Action<IServiceCollection>? configureServices = null) =>
        new(Databases.Settings(mode), environment, services =>
        {
            TestEndpoints.Register(services);
            configureServices?.Invoke(services);
        });

    /// <summary>An Api host in Hybrid mode whose PostgreSQL is an isolated database (exact counts, AC-18).</summary>
    public ConfiguredFactory<Program> CreateFactory(IsolatedDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);

        var settings = Databases.Settings(PersistenceMode.Hybrid);
        settings["ConnectionStrings:Postgres"] = database.AppConnectionString;
        return new(settings, configureServices: TestEndpoints.Register);
    }

    public static ConfiguredFactory<Program> CreateFactory(IReadOnlyDictionary<string, string?> settings, string environment = "Testing") =>
        new(settings, environment, TestEndpoints.Register);
}
