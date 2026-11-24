using System.Net;
using SmartParking.Api.Tests.Fixtures;
using SmartParking.Infrastructure;
using SmartParking.Tests.Common;
using Testcontainers.Redis;

namespace SmartParking.Api.Tests.Features.Health;

[Collection(ApiCollectionDefinition.Name)]
public sealed class HealthEndpointsTests(ApiFixture fixture)
{
    [Fact]
    public async Task GetLive_NoDatabaseReachable_Returns200_AC4()
    {
        // Arrange
        await using var factory = ApiFixture.CreateFactory(DatabaseContainers.UnreachableSettings(PersistenceMode.Hybrid));
        using var client = factory.CreateClient();

        // Act + Assert
        await HealthAssertions.AssertLiveAsync(client, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task GetReady_HybridModeWithAllDatabases_Returns200WithThreeHealthyChecks_AC5()
    {
        // Arrange
        await using var factory = fixture.CreateFactory(PersistenceMode.Hybrid);
        using var client = factory.CreateClient();

        // Act + Assert
        await HealthAssertions.AssertReadyAsync(client, ["postgres", "mongo", "redis"], TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task GetReady_MonolithicMode_Returns200WithOnlyPostgres_AC6()
    {
        // Arrange
        await using var factory = fixture.CreateFactory(PersistenceMode.Monolithic);
        using var client = factory.CreateClient();

        // Act + Assert
        await HealthAssertions.AssertReadyAsync(client, ["postgres"], TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task GetReady_HybridModeWithRedisStopped_Returns503WithRedisUnhealthy_AC7()
    {
        // Arrange: a Redis of its own, started and then stopped, so the shared one stays up.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var redis = new RedisBuilder(ContainerImages.Redis).Build();
        await redis.StartAsync(cancellationToken);
        var settings = fixture.Databases.Settings(PersistenceMode.Hybrid);
        settings["ConnectionStrings:Redis"] = redis.GetConnectionString() + ",connectTimeout=2000";
        settings[HealthCheckTags.TimeoutKey] = "00:00:03";
        await redis.StopAsync(cancellationToken);

        await using var factory = ApiFixture.CreateFactory(settings);
        using var client = factory.CreateClient();

        // Act
        using var ready = await client.GetAsync(new Uri(HealthAssertions.Ready, UriKind.Relative), cancellationToken);
        var report = await HealthAssertions.ReadReportAsync(ready, cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        Assert.Equal("Unhealthy", report.Status);
        Assert.Equal("Unhealthy", report.Checks["redis"].Status);
        Assert.Equal("Healthy", report.Checks["postgres"].Status);
        Assert.Equal("Healthy", report.Checks["mongo"].Status);
        await HealthAssertions.AssertLiveAsync(client, cancellationToken);
    }
}
