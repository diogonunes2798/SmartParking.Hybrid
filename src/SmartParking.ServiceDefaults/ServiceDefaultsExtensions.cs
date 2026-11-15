using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using SmartParking.Application.Diagnostics;

namespace SmartParking.ServiceDefaults;

/// <summary>
/// Cross-cutting host configuration shared by SmartParking.Api and SmartParking.Workers:
/// OpenTelemetry (traces + metrics, Prometheus exporter) and liveness/readiness health checks.
/// See docs/adr/0001 and docs/adr/0002.
/// </summary>
public static class ServiceDefaultsExtensions
{
    /// <summary>Tag for checks that only prove the process is alive (no dependencies).</summary>
    public const string LiveTag = "live";

    /// <summary>Tag for checks of the storage engines the process needs to serve traffic.</summary>
    public const string ReadyTag = "ready";

    public const string LivePath = "/health/live";
    public const string ReadyPath = "/health/ready";
    public const string MetricsPath = "/metrics";

    /// <summary>
    /// Histogram boundaries (seconds) for HTTP server latency: fine-grained around the 200 ms target (UC-03)
    /// and up to the 2 s target (UC-02). See docs/adr/0002.
    /// </summary>
    public static double[] HttpLatencyBucketsSeconds { get; } =
    [
        0.001, 0.0025, 0.005, 0.0075, 0.01, 0.015, 0.02, 0.03, 0.04, 0.05, 0.075,
        0.1, 0.125, 0.15, 0.175, 0.2, 0.225, 0.25, 0.3, 0.4, 0.5, 0.75,
        1, 1.5, 2, 2.5, 5, 10,
    ];

    /// <summary>
    /// Histogram boundaries (seconds) for the pipeline lags of UC-01 (D7, RNF-01: &lt; 1 s p95): fine between 50 ms and
    /// 1 s, coarser up to a minute (recoveries). The default OpenTelemetry boundaries (0, 5, 10, … s) put every
    /// sub-second sample in one bucket, so the p95 could not be computed (review of Phase 4, code finding 1).
    /// </summary>
    public static double[] PipelineLagBucketsSeconds { get; } =
    [
        0.005, 0.01, 0.025, 0.05, 0.075, 0.1, 0.15, 0.2, 0.25, 0.3, 0.4, 0.5, 0.6, 0.7, 0.8, 0.9,
        1, 1.25, 1.5, 2, 3, 5, 10, 30, 60,
    ];

    /// <summary>Histogram boundaries (seconds) for a full read model rebuild (ADR-0008).</summary>
    public static double[] RebuildDurationBucketsSeconds { get; } = [0.1, 0.25, 0.5, 1, 2.5, 5, 10, 30, 60, 120, 300, 600];

    /// <summary>Histograms of the SmartParking meter that measure pipeline lag (D7).</summary>
    public static IReadOnlyList<string> PipelineLagInstruments { get; } =
        ["smartparking.occupancy_log.lag", "smartparking.read_model.lag", "smartparking.read_model.bus_lag"];

    // Instrumentation names emitted natively by Npgsql and EF Core (System.Diagnostics), referenced by name so
    // that ServiceDefaults does not depend on storage drivers (ADR-0001).
    private const string NpgsqlInstrumentationName = "Npgsql";
    private const string EntityFrameworkCoreMeterName = "Microsoft.EntityFrameworkCore";

    /// <summary>Tags to pass to the infrastructure registrations so their checks join readiness.</summary>
    public static IReadOnlyList<string> ReadinessTags { get; } = [ReadyTag];

    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder, string serviceName)
        where TBuilder : IHostApplicationBuilder
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        // InformationalVersion carries the git commit (SourceRevisionId, "+<sha>"), so every measurement
        // can be traced back to the exact code that produced it. Read from this assembly, not the entry
        // assembly, which is the test runner under WebApplicationFactory.
        var serviceVersion = typeof(ServiceDefaultsExtensions).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(serviceName, SmartParkingTelemetry.ServiceNamespace, serviceVersion)
                .AddAttributes([new("deployment.environment.name", builder.Environment.EnvironmentName)]))
            .WithTracing(tracing => tracing
                .AddSource(SmartParkingTelemetry.ActivitySourceName)
                // Database spans of PostgreSQL (Npgsql emits them natively): decompose latency per engine.
                .AddSource(NpgsqlInstrumentationName)
                // Probes and scrapes would drown the traces that matter for the measurements.
                .AddAspNetCoreInstrumentation(o => o.Filter = ctx => !IsInfrastructurePath(ctx.Request.Path))
                .AddHttpClientInstrumentation())
            .WithMetrics(metrics => metrics
                .AddMeter(SmartParkingTelemetry.MeterName)
                .AddMeter(NpgsqlInstrumentationName, EntityFrameworkCoreMeterName)
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                // Default buckets jump from 0.1 s to 0.25 s, so a p95 near the 200 ms target (RNF-01, UC-03)
                // could be interpolated with an error of up to ~150 ms. Finer buckets up to the 2 s UC-02 target.
                .AddView("http.server.request.duration", new ExplicitBucketHistogramConfiguration
                {
                    Boundaries = HttpLatencyBucketsSeconds,
                })
                .AddView(instrument => PipelineLagInstruments.Contains(instrument.Name)
                    ? new ExplicitBucketHistogramConfiguration { Boundaries = PipelineLagBucketsSeconds }
                    : null)
                .AddView("smartparking.read_model.rebuild.duration", new ExplicitBucketHistogramConfiguration
                {
                    Boundaries = RebuildDurationBucketsSeconds,
                })
                .AddPrometheusExporter());

        builder.Services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy(), tags: [LiveTag]);

        return builder;
    }

    /// <summary>Maps <c>/health/live</c>, <c>/health/ready</c> and the Prometheus scraping endpoint.</summary>
    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        var includeDetails = app.Environment.IsDevelopment();

        // DisableHttpMetrics: probes and scrapes (every 5 s per host) must not enter the latency histograms
        // used for the RNF-01 measurements. They are already excluded from traces in AddServiceDefaults.
        app.MapHealthChecks(LivePath, new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains(LiveTag),
            ResponseWriter = (ctx, report) => WriteHealthResponse(ctx, report, includeDetails),
        }).DisableHttpMetrics();

        app.MapHealthChecks(ReadyPath, new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains(ReadyTag),
            ResponseWriter = (ctx, report) => WriteHealthResponse(ctx, report, includeDetails),
        }).DisableHttpMetrics();

        app.MapPrometheusScrapingEndpoint(MetricsPath).DisableHttpMetrics();

        return app;
    }

    private static bool IsInfrastructurePath(PathString path) =>
        path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments(MetricsPath, StringComparison.OrdinalIgnoreCase);

    private static Task WriteHealthResponse(HttpContext context, HealthReport report, bool includeDetails)
    {
        context.Response.ContentType = "application/json; charset=utf-8";

        // Error details can reveal topology (hosts, ports); only shown in Development.
        var payload = new
        {
            status = report.Status.ToString(),
            totalDurationMs = report.TotalDuration.TotalMilliseconds,
            checks = report.Entries.Select(e => new
            {
                name = e.Key,
                status = e.Value.Status.ToString(),
                durationMs = e.Value.Duration.TotalMilliseconds,
                description = includeDetails ? e.Value.Description ?? e.Value.Exception?.Message : null,
            }),
        };

        return context.Response.WriteAsync(JsonSerializer.Serialize(payload, JsonOptions));
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
}
