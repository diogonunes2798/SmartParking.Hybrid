using SmartParking.Infrastructure;
using SmartParking.Tests.Common;
using SmartParking.Workers.Tests.Fixtures;

namespace SmartParking.Workers.Tests.Features.Health;

[Collection(WorkersCollectionDefinition.Name)]
public sealed class WorkersHealthTests(WorkersFixture fixture)
{
    [Theory]
    [InlineData(PersistenceMode.Hybrid)]
    [InlineData(PersistenceMode.Monolithic)]
    public async Task GetLive_NoDatabaseReachable_Returns200_AC8(PersistenceMode mode)
    {
        // Arrange
        await using var factory = new ConfiguredFactory<Program>(DatabaseContainers.UnreachableSettings(mode));
        using var client = factory.CreateClient();

        // Act + Assert
        await HealthAssertions.AssertLiveAsync(client, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task GetReady_HybridModeWithAllDatabases_Returns200WithThreeHealthyChecks_AC8()
    {
        // Arrange
        await using var factory = fixture.CreateFactory(PersistenceMode.Hybrid);
        using var client = factory.CreateClient();

        // Act + Assert
        await HealthAssertions.AssertReadyAsync(client, ["postgres", "mongo", "redis"], TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task GetReady_MonolithicMode_Returns200WithOnlyPostgres_AC8()
    {
        // Arrange
        await using var factory = fixture.CreateFactory(PersistenceMode.Monolithic);
        using var client = factory.CreateClient();

        // Act + Assert
        await HealthAssertions.AssertReadyAsync(client, ["postgres"], TestContext.Current.CancellationToken);
    }
}
