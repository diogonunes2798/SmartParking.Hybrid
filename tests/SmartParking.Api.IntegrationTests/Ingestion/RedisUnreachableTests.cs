using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using SmartParking.Application.Ingestion;
using CacheRegistration = SmartParking.Infrastructure.Cache.DependencyInjection;
using DocumentsRegistration = SmartParking.Infrastructure.Documents.DependencyInjection;
using RelationalRegistration = SmartParking.Infrastructure.Relational.DependencyInjection;

namespace SmartParking.Api.IntegrationTests.Ingestion;

/// <summary>
/// Review of Phase 3, finding 2: with a Redis that does not answer at all (not a refused connection), the synchronous
/// <c>ConnectionMultiplexer.Connect</c> blocked the start of the host for about ConnectTimeout + 3 s. A stopped
/// Testcontainers Redis refuses connections at once, so it does not reproduce this; a non-routable address does.
/// </summary>
[Collection(IngestionTestGroup.Name)]
public sealed class RedisUnreachableTests(IngestionFixture fixture)
{
    // TEST-NET-1 (RFC 5737): reserved for documentation, never answers. Short client timeouts keep the test fast.
    private const string Unreachable = "192.0.2.1:6379,connectTimeout=3000,asyncTimeout=1000,syncTimeout=1000";

    private static readonly TimeSpan FastStart = TimeSpan.FromSeconds(2);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [HybridFact]
    public async Task Api_starts_without_waiting_for_redis_and_ingestion_answers_503()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(ApiFixture.EnvironmentName);
            builder.UseSetting(
                $"ConnectionStrings:{RelationalRegistration.ConnectionStringName}",
                "Host=postgres.invalid;Database=unused;Username=unused;Password=unused");
            builder.UseSetting($"ConnectionStrings:{DocumentsRegistration.ConnectionStringName}", fixture.Mongo.GetConnectionString());
            builder.UseSetting($"ConnectionStrings:{CacheRegistration.ConnectionStringName}", Unreachable);
        });

        var watch = Stopwatch.StartNew();
        var client = factory.CreateClient(); // starts the host
        var live = await client.GetAsync("/health/live", Ct);
        watch.Stop();

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.True(watch.Elapsed < FastStart, $"Host start and /health/live took {watch.Elapsed.TotalSeconds:F1} s.");

        // Readiness answers at once while the first connection attempt is still running.
        watch.Restart();
        var ready = await client.GetAsync("/health/ready", Ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        Assert.True(watch.Elapsed < FastStart, $"/health/ready took {watch.Elapsed.TotalSeconds:F1} s.");

        var ingest = await client.PostAsJsonAsync("/api/ingest/occupancy", new[] { IngestionFixture.Event() }, IngestionFixture.Json, Ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ingest.StatusCode);
    }

    [HybridFact]
    public async Task Consumer_host_starts_without_waiting_for_redis()
    {
        var watch = Stopwatch.StartNew();
        await using var consumer = await fixture.StartConsumerAsync<OccupancyLogWriter>(
            o =>
            {
                o.Stream = "test:unreachable";
                o.Group = "test-group";
            },
            redisConnectionString: Unreachable);
        watch.Stop();

        Assert.True(watch.Elapsed < FastStart, $"Consumer host start took {watch.Elapsed.TotalSeconds:F1} s.");
    }
}
