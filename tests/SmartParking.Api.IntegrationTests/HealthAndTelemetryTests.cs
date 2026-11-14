using System.Net;
using System.Net.Http.Json;

namespace SmartParking.Api.IntegrationTests;

/// <summary>
/// Phase 1 acceptance: the API starts against the three engines and exposes liveness/readiness,
/// the OpenAPI document and the Prometheus metrics endpoint (measurement base for RNF-01/RNF-02).
/// </summary>
public sealed class HealthAndTelemetryTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private readonly HttpClient _client = fixture.Factory.CreateClient();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Liveness_is_healthy_and_checks_no_dependencies()
    {
        var response = await _client.GetAsync("/health/live", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<HealthResponse>(Ct);
        Assert.NotNull(body);
        Assert.Equal("Healthy", body.Status);
        var check = Assert.Single(body.Checks);
        Assert.Equal("self", check.Name);
    }

    [Fact]
    public async Task Readiness_is_healthy_when_all_storage_engines_are_up()
    {
        var response = await _client.GetAsync("/health/ready", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<HealthResponse>(Ct);
        Assert.NotNull(body);
        Assert.Equal("Healthy", body.Status);
        Assert.Equal(["mongodb", "postgres", "redis"], body.Checks.Select(c => c.Name).Order());
        Assert.All(body.Checks, c => Assert.Equal("Healthy", c.Status));
    }

    [Fact]
    public async Task OpenApi_document_is_served_outside_production()
    {
        var response = await _client.GetAsync("/openapi/v1.json", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Metrics_endpoint_exposes_http_server_metrics_with_service_resource()
    {
        // Generate at least one measured request before scraping (health/metrics are not measured).
        (await _client.GetAsync("/openapi/v1.json", Ct)).EnsureSuccessStatusCode();

        var text = await ScrapeAsync();

        Assert.Contains("http_server_request_duration_seconds", text, StringComparison.Ordinal);
        Assert.Contains("http_route=\"/openapi/{documentName}.json\"", text, StringComparison.Ordinal);
        Assert.Contains("service_name=\"smartparking-api\"", text, StringComparison.Ordinal);
        Assert.Contains("service_namespace=\"smartparking\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Probes_and_scrapes_are_excluded_from_latency_histograms()
    {
        (await _client.GetAsync("/health/live", Ct)).EnsureSuccessStatusCode();
        (await _client.GetAsync("/health/ready", Ct)).EnsureSuccessStatusCode();
        await ScrapeAsync();

        // Only the latency histogram matters; routing metrics (aspnetcore_routing_*) may still list the routes.
        var latencyLines = (await ScrapeAsync())
            .Split('\n')
            .Where(line => line.StartsWith("http_server_request_duration_seconds", StringComparison.Ordinal))
            .ToArray();

        Assert.DoesNotContain(latencyLines, line => line.Contains("http_route=\"/health/", StringComparison.Ordinal));
        Assert.DoesNotContain(latencyLines, line => line.Contains("http_route=\"/metrics\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Latency_histogram_uses_fine_buckets_around_the_200_ms_target()
    {
        (await _client.GetAsync("/openapi/v1.json", Ct)).EnsureSuccessStatusCode();

        var text = await ScrapeAsync();

        // Boundaries that do not exist in the default HTTP buckets (see ServiceDefaultsExtensions).
        Assert.Contains("http_server_request_duration_seconds_bucket{", text, StringComparison.Ordinal);
        Assert.Contains("le=\"0.175\"", text, StringComparison.Ordinal);
        Assert.Contains("le=\"0.225\"", text, StringComparison.Ordinal);
    }

    private async Task<string> ScrapeAsync()
    {
        var response = await _client.GetAsync("/metrics", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync(Ct);
    }
}
