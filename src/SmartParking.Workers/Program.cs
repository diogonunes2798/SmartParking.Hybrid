using SmartParking.Application.Analytics;
using SmartParking.Application.Availability;
using SmartParking.Application.Ingestion;
using SmartParking.Application.Outbox;
using SmartParking.Application.Retention;
using SmartParking.Infrastructure.Cache;
using SmartParking.Infrastructure.Documents;
using SmartParking.Infrastructure.Messaging;
using SmartParking.Infrastructure.Relational;
using SmartParking.Infrastructure.Relational.Monolithic;
using SmartParking.ServiceDefaults;
using SmartParking.Workers.Analytics;
using SmartParking.Workers.Monolithic;
using SmartParking.Workers.Outbox;
using SmartParking.Workers.ReadModel;
using SmartParking.Workers.Retention;

// `dotnet run --project src/SmartParking.Workers -- rebuild-read-model` rebuilds the availability read model from
// PostgreSQL and MongoDB and exits (ADR-0008; benchmarks start from a known state). It may run while the Workers run:
// the rebuild lock holds their projectors back meanwhile (review of Phase 4, finding 1).
const string RebuildCommand = "rebuild-read-model";

// `dotnet run --project src/SmartParking.Workers -- aggregate-occupancy --from 2026-11-16T00:00Z --to 2026-11-17T00:00Z`
// aggregates that range of whole UTC hours into the summary table and exits (UC-04, ADR-0010): backfills, and the
// benchmarks of Phase 9. Safe while the Workers run (upsert); it does not move the worker's checkpoint.
const string AggregateCommand = "aggregate-occupancy";

// `dotnet run --project src/SmartParking.Workers -- apply-retention` runs the retention once (archive, snapshot, purge
// horizon, TTL guard or DROP of partitions) and exits (RNF-05, Phase 10, ADR-0013): the retention benchmark scenario.
const string RetentionCommand = "apply-retention";
var command = args.Length > 0 && args[0] is RebuildCommand or AggregateCommand or RetentionCommand ? args[0] : null;
var rebuildOnly = command is not null;

// The workers host is a (minimal) web host so that it exposes /health and /metrics like the API:
// readiness and metrics of the consumers are needed for RNF-02/RNF-03 measurements.
// The arguments after a command reach the configuration («--from X» is the key «from»).
var builder = WebApplication.CreateBuilder(rebuildOnly ? args[1..] : args);

builder.AddServiceDefaults("smartparking-workers");

// Persistence:Mode (spec §7, ADR-0012). Monolithic: PostgreSQL only. There is no bus to consume and no read model to
// project (the API writes the events and the spot states synchronously), so only the outbox relay, the analytics
// aggregation and the partition maintenance run here.
var monolithic = builder.Configuration.GetPersistenceMode() == PersistenceMode.Monolithic;
if (monolithic && command == RebuildCommand)
{
    Console.Error.WriteLine($"'{RebuildCommand}' does not apply to Persistence:Mode=Monolithic: there is no read model to rebuild.");
    Environment.ExitCode = 1;
    return;
}

var readiness = ServiceDefaultsExtensions.ReadinessTags;
builder.Services.AddRelationalPersistence(builder.Configuration, readiness);
if (monolithic)
{
    builder.Services.AddMonolithicPersistence(builder.Configuration);
}
else
{
    builder.Services
        .AddDocumentPersistence(builder.Configuration, readiness)
        .AddCacheStore(builder.Configuration, readiness)
        .AddEventBus(builder.Configuration); // also the consumer group positions used by the rebuild

    // Availability read model (UC-03, RF-03, Phase 4, ADR-0008): rebuild, initial load and the two projectors.
    builder.Services.AddSingleton<ReadModelMetrics>();
    builder.Services.AddSingleton<ReadModelGate>();
    builder.Services.AddScoped<ReadModelRebuilder>();
}

// Occupancy aggregation (UC-04, RF-05, Phase 6, D4 default, ADR-0010): MongoDB log → analytics.occupancy_hourly.
// Settings: Analytics (Enabled, Interval, AllowedLateness, ReaggregateHours, MaxHoursPerRun, ...).
var analytics = builder.Configuration.GetSection(AnalyticsOptions.SectionName).Get<AnalyticsOptions>() ?? new();
analytics.Validate();
builder.Services.AddSingleton(analytics);
builder.Services.AddSingleton<AnalyticsMetrics>();
builder.Services.AddScoped<OccupancyAggregator>();

// Retention of the raw events (RNF-05, Phase 10, ADR-0013), both variants. Settings: Retention (RawEvents, ArchiveDelay,
// GuardMargin, ArchivePath, ...). Also wraps the event log so that aggregations and rebuilds read the spot state snapshot.
builder.Services.AddRetention(builder.Configuration);
var retention = builder.Configuration.GetSection(RetentionOptions.SectionName).Get<RetentionOptions>() ?? new();

if (!rebuildOnly && monolithic)
{
    // Monthly partitions of monolith.occupancy_events, created ahead (ADR-0012). Settings: Persistence:Monolithic.
    builder.Services.AddHostedService<PartitionMaintenanceService>();
}

if (!rebuildOnly && !monolithic)
{
    // UC-01 step 3 (Phase 3): occupancy events from the bus (D2) to the immutable log in MongoDB (D3).
    // Settings: Messaging:Consumers:occupancy-log (see RedisStreamConsumerOptions and ADR-0006).
    builder.Services.AddSingleton<OccupancyIngestionMetrics>();
    builder.Services.AddStreamConsumer<OccupancyLogWriter>(
        builder.Configuration,
        OccupancyLogConsumer.Name,
        options =>
        {
            options.Stream = OccupancyEventMessage.Topic;
            options.Group = OccupancyLogConsumer.Name;
        });

    // Loads the read model if Redis has none, then lets the projectors start (ReadModelGate).
    builder.Services.AddHostedService<ReadModelInitializer>();

    // UC-01 step 4: second consumer group on the same stream → spot state, counters, pub/sub.
    // Settings: Messaging:Consumers:availability.
    builder.Services.AddStreamConsumer<AvailabilityProjector>(
        builder.Configuration,
        AvailabilityConsumers.Occupancy,
        options =>
        {
            options.Stream = OccupancyEventMessage.Topic;
            options.Group = AvailabilityConsumers.Occupancy;
        });

    // RF-06 → read model: configuration events relayed from the outbox (relay below, Phase 5).
    // Settings: Messaging:Consumers:availability-configuration.
    builder.Services.AddStreamConsumer<ConfigurationProjector>(
        builder.Configuration,
        AvailabilityConsumers.Configuration,
        options =>
        {
            options.Stream = ConfigurationEventMessage.Topic;
            options.Group = AvailabilityConsumers.Configuration; // own name: own metrics (review of Phase 4, finding 6)
        });
}

if (!rebuildOnly)
{
    // Outbox relay (UC-02, RF-06, Phase 5, ADR-0009): PostgreSQL outbox → bus (D2), FOR UPDATE SKIP LOCKED so that
    // several Workers instances can run it. Settings: Outbox (Enabled, BatchSize, PollInterval, ...).
    var outbox = builder.Configuration.GetSection(OutboxRelayOptions.SectionName).Get<OutboxRelayOptions>() ?? new();
    outbox.Validate();
    builder.Services.Configure<OutboxRelayOptions>(builder.Configuration.GetSection(OutboxRelayOptions.SectionName));
    builder.Services.AddSingleton<OutboxMetrics>();
    builder.Services.AddScoped<OutboxRelay>();
    if (outbox.Enabled)
    {
        builder.Services.AddHostedService<OutboxRelayService>();
    }

    if (analytics.Enabled)
    {
        builder.Services.AddHostedService<OccupancyAggregationService>();
    }

    if (retention.Enabled)
    {
        builder.Services.AddHostedService<RetentionService>();
    }
}

var app = builder.Build();

if (rebuildOnly)
{
    using var cancel = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cancel.Cancel();
    };

    var scope = app.Services.CreateAsyncScope();
    await using (scope)
    {
        try
        {
            if (command == AggregateCommand)
            {
                var from = ParseHour(builder.Configuration["from"], "--from");
                var to = ParseHour(builder.Configuration["to"], "--to");
                var report = await scope.ServiceProvider.GetRequiredService<OccupancyAggregator>().AggregateAsync(from, to, cancel.Token);
                Console.WriteLine(
                    $"Occupancy aggregated from {from:O} to {to:O}: {report.Facilities} facilities, {report.Hours} hours, " +
                    $"{report.Rows} rows, {report.Events} events in {report.Duration.TotalMilliseconds:F0} ms.");
            }
            else if (command == RetentionCommand)
            {
                var report = await scope.ServiceProvider.GetRequiredService<OccupancyRetention>().RunOnceAsync(cancel.Token);
                Console.WriteLine(
                    $"Retention applied: {report.DaysArchived} days archived ({report.EventsArchived} events), " +
                    $"archived until {report.ArchivedUntil:O}, purge horizon {report.PurgeHorizon:O}, " +
                    $"TTL {report.Enforcement?.ExpireAfterSeconds} s, partitions dropped: " +
                    $"{string.Join(' ', report.Enforcement?.DroppedPartitions ?? [])}" +
                    (report.Blocked is { } blocked ? $"; waiting: {blocked}." : "."));
                if (report.Blocked is not null)
                {
                    Environment.ExitCode = 3; // the benchmark must know that retention did not reach its target
                }
            }
            else
            {
                var report = await scope.ServiceProvider.GetRequiredService<ReadModelRebuilder>().RebuildAsync(cancel.Token);
                Console.WriteLine(
                    $"Read model rebuilt: {report.Facilities} facilities, {report.Spots} spots, " +
                    $"{report.Events} latest events ({report.EventsApplied} applied) in {report.Duration.TotalMilliseconds:F0} ms.");
            }
        }
        catch (Exception ex) when (ex is RebuildInProgressException or ArgumentException)
        {
            Console.Error.WriteLine(ex.Message);
            Environment.ExitCode = 1;
        }
    }

    return;
}

app.MapDefaultEndpoints();

await app.RunAsync();

static DateTimeOffset ParseHour(string? value, string name) =>
    IsoInstant.TryParse(value, out var instant)
    && OccupancyIntervalCalculator.IsWholeHour(instant)
        ? instant
        : throw new ArgumentException($"{name} must be a whole hour in ISO 8601 (e.g. 2026-11-16T00:00:00Z), got '{value}'.");
