using SmartParking.Application.Features.Ingestion;
using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Formatting.Compact;

namespace SmartParking.Infrastructure.Hosting;

/// <summary>Cross-cutting web foundation shared by Api and Workers (spec 000, ADR 0002).</summary>
public static class HostingDefaults
{
    /// <summary>OTLP export is enabled only when this key has a value.</summary>
    public const string OtlpEndpointKey = "OTEL_EXPORTER_OTLP_ENDPOINT";

    /// <summary>Serilog, OpenTelemetry, ProblemDetails, global exception handler and the health endpoints.</summary>
    public static WebApplicationBuilder AddHostingDefaults(this WebApplicationBuilder builder, string serviceName)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.AddObservability(serviceName);

        builder.Services.AddProblemDetails(options =>
            options.CustomizeProblemDetails = context =>
                context.ProblemDetails.Extensions.TryAdd(
                    "traceId",
                    Activity.Current?.Id ?? context.HttpContext.TraceIdentifier));

        builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
        builder.Services.AddHealthChecks();
        builder.Services.AddEndpointGroups(typeof(HostingDefaults).Assembly);

        return builder;
    }

    /// <summary>Exception handler first, so every later middleware and endpoint is covered.</summary>
    public static WebApplication UseHostingDefaults(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.UseExceptionHandler();
        app.UseStatusCodePages();

        return app;
    }

    private static void AddObservability(this WebApplicationBuilder builder, string serviceName)
    {
        // Serilog owns the pipeline; writeToProviders forwards each event to the OpenTelemetry
        // logger provider, which exports it over OTLP when an endpoint is configured.
        builder.Logging.ClearProviders();
        builder.Services.AddSerilog(
            (services, logger) => logger
                .ReadFrom.Configuration(builder.Configuration)
                .ReadFrom.Services(services)
                .Enrich.FromLogContext()
                .Enrich.WithProperty("Service", serviceName)
                .WriteTo.Console(new CompactJsonFormatter()),
            writeToProviders: true);

        var telemetry = builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName))
            .WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation())
            .WithMetrics(metrics => metrics
                .AddMeter(IngestionMetrics.MeterName)
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation())
            .WithLogging();

        if (!string.IsNullOrWhiteSpace(builder.Configuration[OtlpEndpointKey]))
        {
            telemetry.UseOtlpExporter();
        }
    }
}
