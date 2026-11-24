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
    public DatabaseContainers Databases { get; } = new();

    public ValueTask InitializeAsync() => Databases.InitializeAsync();

    public ValueTask DisposeAsync() => Databases.DisposeAsync();

    public ConfiguredFactory<Program> CreateFactory(
        PersistenceMode mode = PersistenceMode.Hybrid,
        string environment = "Testing",
        Action<IServiceCollection>? configureServices = null) =>
        new(Databases.Settings(mode), environment, services =>
        {
            TestEndpoints.Register(services);
            configureServices?.Invoke(services);
        });

    public static ConfiguredFactory<Program> CreateFactory(IReadOnlyDictionary<string, string?> settings, string environment = "Testing") =>
        new(settings, environment, TestEndpoints.Register);
}
