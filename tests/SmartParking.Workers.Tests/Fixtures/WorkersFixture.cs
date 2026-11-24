using SmartParking.Infrastructure;
using SmartParking.Tests.Common;

namespace SmartParking.Workers.Tests.Fixtures;

/// <summary>Starts the three databases once for the Workers host tests.</summary>
public sealed class WorkersFixture : IAsyncLifetime
{
    public DatabaseContainers Databases { get; } = new();

    public ValueTask InitializeAsync() => Databases.InitializeAsync();

    public ValueTask DisposeAsync() => Databases.DisposeAsync();

    public ConfiguredFactory<Program> CreateFactory(PersistenceMode mode) => new(Databases.Settings(mode));
}
