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

    private ConfiguredFactory<Program>? sessions;

    private ConfiguredFactory<Program>? gated;

    public DatabaseContainers Databases { get; } = new();

    /// <summary>One Api host in Hybrid mode shared by the feature tests (spec 001), created on first use.</summary>
    public ConfiguredFactory<Program> Hybrid => hybrid ??= CreateFactory();

    /// <summary>The clock of the <see cref="Sessions"/> and <see cref="Gated"/> hosts (spec 002). Each test sets its own instant.</summary>
    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 12, 2, 10, 0, 0, TimeSpan.Zero));

    /// <summary>The barrier of the <see cref="Gated"/> host (AC-11, AC-16).</summary>
    internal ConcurrencyGate Gate { get; } = new();

    /// <summary>An Api host in Hybrid mode with the <see cref="Clock"/> (spec 002), created on first use.</summary>
    public ConfiguredFactory<Program> Sessions => sessions ??= CreateFactory(configureServices: UseClock);

    /// <summary>Like <see cref="Sessions"/>, with the <see cref="Gate"/> in front of the unit of work.</summary>
    public ConfiguredFactory<Program> Gated => gated ??= CreateFactory(configureServices: services =>
    {
        UseClock(services);
        GatedUnitOfWork.Decorate(services, Gate);
    });

    public ValueTask InitializeAsync() => Databases.InitializeAsync();

    public async ValueTask DisposeAsync()
    {
        foreach (var factory in new[] { hybrid, sessions, gated })
        {
            if (factory is not null)
            {
                await factory.DisposeAsync();
            }
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
    public ConfiguredFactory<Program> CreateFactory(IsolatedDatabase database, Action<IServiceCollection>? configureServices = null)
    {
        ArgumentNullException.ThrowIfNull(database);

        var settings = Databases.Settings(PersistenceMode.Hybrid);
        settings["ConnectionStrings:Postgres"] = database.AppConnectionString;
        return new(settings, configureServices: services =>
        {
            TestEndpoints.Register(services);
            configureServices?.Invoke(services);
        });
    }

    public static ConfiguredFactory<Program> CreateFactory(IReadOnlyDictionary<string, string?> settings, string environment = "Testing") =>
        new(settings, environment, TestEndpoints.Register);

    /// <summary>Replaces the system clock with <see cref="Clock"/> (the last registration wins).</summary>
    public void UseClock(IServiceCollection services) => services.AddSingleton<TimeProvider>(Clock);
}
