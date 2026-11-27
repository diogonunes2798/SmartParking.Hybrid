using System.CommandLine;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SmartParking.Application;
using SmartParking.Application.Abstractions.Messaging;
using SmartParking.Application.Features.Availability;
using SmartParking.Infrastructure;

namespace SmartParking.ProjectionRebuilder;

/// <summary>
/// <c>ProjectionRebuilder</c>: removes the <c>avail:</c> keys and rebuilds the availability view from the configuration
/// in PostgreSQL and the last event of each spot in MongoDB (spec 004, «Rebuild»). Run offline, with the projector
/// stopped. Configuration as the Workers: <c>projectionrebuilder.json</c> next to the tool, then environment variables
/// (<c>ConnectionStrings__Postgres</c> with the <c>smartparking_workers</c> login, <c>__Mongo</c>, <c>__Redis</c>).
/// </summary>
internal static partial class RebuilderCommand
{
    public const string SettingsFile = "projectionrebuilder.json";

    /// <param name="settings">Configuration that overrides the file and the environment (tests).</param>
    /// <param name="configureServices">Last word on the services (tests).</param>
    public static RootCommand Create(
        IReadOnlyDictionary<string, string?>? settings = null,
        Action<IServiceCollection>? configureServices = null)
    {
        var root = new RootCommand("Rebuilds the Redis availability view from PostgreSQL and MongoDB (spec 004).");

        root.SetAction(async (_, cancellationToken) =>
        {
            using var host = BuildHost(settings, configureServices);
            var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(RebuilderCommand));

            if (!PersistenceOptions.TryParseMode(host.Services.GetRequiredService<IConfiguration>()[PersistenceOptions.ModeKey], out var mode)
                || mode != PersistenceMode.Hybrid)
            {
                LogNotHybrid(logger);
                return ExitCodes.NotHybrid;
            }

            try
            {
                await using var scope = host.Services.CreateAsyncScope();
                var handler = scope.ServiceProvider
                    .GetRequiredService<ICommandHandler<RebuildAvailability.Command, RebuildAvailability.Summary>>();
                var result = await handler.HandleAsync(new RebuildAvailability.Command(), cancellationToken);

                var summary = result.Value;
                LogRebuilt(logger, summary.Facilities, summary.Zones, summary.Spots, summary.EventsRead, summary.Applied,
                    summary.Duplicates, summary.Stale, summary.UnknownSpots, summary.Duration.TotalSeconds);
                return ExitCodes.Success;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return ExitCodes.Cancelled;
            }
#pragma warning disable CA1031 // Process boundary: any store failure ends the run with an exit code; the rebuild is idempotent.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                LogFailed(logger, exception);
                return ExitCodes.RebuildFailed;
            }
        });

        return root;
    }

    private static IHost BuildHost(IReadOnlyDictionary<string, string?>? settings, Action<IServiceCollection>? configureServices)
    {
        // System.CommandLine owns the command line; the content root is the tool's folder, never the working directory.
        // Sources, last wins: the tool's file, the environment, then the overrides of the tests.
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            ContentRootPath = AppContext.BaseDirectory,
            DisableDefaults = true,
        });
        builder.Configuration
            .AddJsonFile(SettingsFile, optional: true)
            .AddEnvironmentVariables();
        if (settings is not null)
        {
            builder.Configuration.AddInMemoryCollection(settings);
        }

        builder.Logging.AddConfiguration(builder.Configuration.GetSection("Logging"));
        builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

        builder.Services
            .AddApplication()
            .AddInfrastructure(builder.Configuration);
        configureServices?.Invoke(builder.Services);

        // Built, never started: no hosted service runs, and the handler creates the index itself.
        return builder.Build();
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information,
        Message = "Availability view rebuilt: {Facilities} facilities, {Zones} zones, {Spots} spots; {EventsRead} last events read " +
            "({Applied} applied, {Duplicates} duplicate, {Stale} stale, {UnknownSpots} unknown spot) in {DurationSeconds} s")]
    private static partial void LogRebuilt(
        ILogger logger, int facilities, int zones, int spots, int eventsRead, int applied, int duplicates, int stale,
        int unknownSpots, double durationSeconds);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error, Message = "Persistence:Mode is not Hybrid: the availability view exists only in the hybrid mode")]
    private static partial void LogNotHybrid(ILogger logger);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "Availability rebuild failed; the view may be partial. Run the rebuild again")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
