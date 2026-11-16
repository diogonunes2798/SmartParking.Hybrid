using System.Net;
using System.Net.Http.Json;

namespace SmartParking.Api.IntegrationTests;

/// <summary>
/// Basis of the RNF-03 failover scenario: when a storage engine is down, readiness must report it
/// (HTTP 503) within the health-check timeout, while liveness stays healthy so the process is not restarted.
/// Every test creates its own <see cref="ApiFixture"/> because it stops a container.
/// </summary>
public sealed class ReadinessFailureTests
{
    /// <summary>Per-check timeout (3 s, see the DependencyInjection classes) plus scheduling margin.</summary>
    private const double MaxReadinessDurationMs = 4_500;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // The engines of the variant under test (Phase 8): PostgreSQL only in the monolithic one.
    public static TheoryData<string> Engines => new(ApiFixture.ReadinessChecks(TestMode.Current).ToArray());

    [Theory]
    [MemberData(nameof(Engines))]
    public async Task Readiness_reports_the_engine_that_stops_and_liveness_stays_healthy(string engine)
    {
        await using var fixture = new ApiFixture();
        await fixture.InitializeAsync();
        var client = fixture.Factory.CreateClient();
        (await client.GetAsync("/health/ready", Ct)).EnsureSuccessStatusCode();

        await fixture.ContainerFor(engine).StopAsync(Ct);

        await AssertOnlyEngineUnhealthyAsync(client, engine);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live", Ct)).StatusCode);
    }

    /// <summary>
    /// The API must start and answer readiness quickly even if Redis is already down at boot
    /// (the connection is opened at startup, not on the first probe).
    /// </summary>
    [HybridFact]
    public async Task Api_starts_and_reports_unready_when_redis_is_down_at_boot()
    {
        await using var fixture = new ApiFixture();
        await fixture.InitializeAsync();
        await fixture.Redis.StopAsync(Ct);

        var client = fixture.Factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live", Ct)).StatusCode);
        await AssertOnlyEngineUnhealthyAsync(client, "redis");
    }

    private static async Task AssertOnlyEngineUnhealthyAsync(HttpClient client, string engine)
    {
        var ready = await client.GetAsync("/health/ready", Ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        var body = await ready.Content.ReadFromJsonAsync<HealthResponse>(Ct);
        Assert.NotNull(body);
        Assert.Equal("Unhealthy", body.Status);
        Assert.Equal("Unhealthy", body.Checks.Single(c => c.Name == engine).Status);
        Assert.All(body.Checks.Where(c => c.Name != engine), c => Assert.Equal("Healthy", c.Status));

        // Regression guard: each check must honour its own timeout, not the driver's default
        // (Redis client 5 s, MongoDB server selection 30 s).
        Assert.True(
            body.TotalDurationMs < MaxReadinessDurationMs,
            $"Readiness took {body.TotalDurationMs:F0} ms with {engine} down; expected < {MaxReadinessDurationMs} ms.");
    }
}
