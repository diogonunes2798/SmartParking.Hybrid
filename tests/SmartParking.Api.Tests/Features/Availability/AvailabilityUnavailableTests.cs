using System.Net;
using Microsoft.Extensions.DependencyInjection;
using SmartParking.Api.Tests.Fixtures;
using SmartParking.Application.Abstractions.Availability;
using SmartParking.Domain.Occupancy;
using SmartParking.Infrastructure;
using SmartParking.Tests.Common;
using StackExchange.Redis;
using Testcontainers.Redis;
using static SmartParking.Api.Tests.Fixtures.AvailabilityApi;

namespace SmartParking.Api.Tests.Features.Availability;

/// <summary>
/// Redis unavailable (spec 004, AC-20): the queries answer 503 with ProblemDetails and <c>Retry-After</c>, with no
/// fallback to PostgreSQL; the projector throws to its caller (the F6 transport decides the retry). A Redis of its own
/// is started and stopped, so the shared one stays up.
/// </summary>
[Collection(ApiCollectionDefinition.Name)]
public sealed class AvailabilityUnavailableTests(ApiFixture fixture)
{
    [Theory]
    [InlineData("/api/availability?operatorId=0193a1b0-0000-7000-8000-000000000001")]
    [InlineData("/api/availability?facilityId=0193a1b0-0000-7000-8000-000000000009")]
    [InlineData("/api/availability/nearby?lat=40.2&lon=-8.4&radiusMeters=1000")]
    public async Task Queries_RedisStopped_Return503WithRetryAfter_AC20(string route)
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        var (redis, settings) = await StoppedRedisAsync(cancellationToken);
        await using var stopped = redis;
        await using var factory = ApiFixture.CreateFactory(settings);
        using var client = factory.CreateClient();

        // Act
        using var response = await client.GetAsync(new Uri(route, UriKind.Relative), cancellationToken);

        // Assert
        var problem = await AssertProblemAsync(response, HttpStatusCode.ServiceUnavailable, cancellationToken);
        Assert.Equal("Availability.ViewUnavailable", problem.GetProperty("title").GetString());
        Assert.Equal(TimeSpan.FromSeconds(1), response.Headers.RetryAfter?.Delta);
    }

    [Fact]
    public async Task Projector_RedisStopped_ThrowsToCaller_AC20()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        var (redis, settings) = await StoppedRedisAsync(cancellationToken);
        await using var stopped = redis;
        await using var factory = ApiFixture.CreateFactory(settings);
        var projector = factory.Services.GetRequiredService<IOccupancyProjector>();
        var occupancyEvent = new OccupancyEvent(
            Guid.NewGuid(), Guid.NewGuid(), "HW-unavailable", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), OccupancyEventType.Occupied, T, T, null);

        // Act
        var exception = await Record.ExceptionAsync(() => projector.ApplyAsync(occupancyEvent, publishChanges: true, cancellationToken));

        // Assert
        Assert.True(exception is RedisConnectionException or RedisTimeoutException, $"Expected a Redis failure, got {exception?.GetType().Name ?? "none"}.");
    }

    /// <summary>A Redis started and then stopped, with host settings that point at it (the port is read before the stop).</summary>
    private async Task<(RedisContainer Redis, Dictionary<string, string?> Settings)> StoppedRedisAsync(CancellationToken cancellationToken)
    {
        var redis = new RedisBuilder(ContainerImages.Redis).Build();
        await redis.StartAsync(cancellationToken);
        var settings = fixture.Databases.Settings(PersistenceMode.Hybrid);
        settings["ConnectionStrings:Redis"] = redis.GetConnectionString() + ",connectTimeout=2000,syncTimeout=2000,asyncTimeout=2000";
        await redis.StopAsync(cancellationToken);
        return (redis, settings);
    }
}
