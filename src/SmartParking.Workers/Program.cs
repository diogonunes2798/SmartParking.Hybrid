using SmartParking.Application.Availability;
using SmartParking.Application.Ingestion;
using SmartParking.Infrastructure.Cache;
using SmartParking.Infrastructure.Documents;
using SmartParking.Infrastructure.Messaging;
using SmartParking.Infrastructure.Relational;
using SmartParking.ServiceDefaults;
using SmartParking.Workers.ReadModel;

// `dotnet run --project src/SmartParking.Workers -- rebuild-read-model` rebuilds the availability read model from
// PostgreSQL and MongoDB and exits (ADR-0008; benchmarks start from a known state). It may run while the Workers run:
// the rebuild lock holds their projectors back meanwhile (review of Phase 4, finding 1).
const string RebuildCommand = "rebuild-read-model";
var rebuildOnly = args.Length > 0 && args[0] == RebuildCommand;

// The workers host is a (minimal) web host so that it exposes /health and /metrics like the API:
// readiness and metrics of the consumers are needed for RNF-02/RNF-03 measurements.
var builder = WebApplication.CreateBuilder(rebuildOnly ? args[1..] : args);

builder.AddServiceDefaults("smartparking-workers");

var readiness = ServiceDefaultsExtensions.ReadinessTags;
builder.Services
    .AddRelationalPersistence(builder.Configuration, readiness)
    .AddDocumentPersistence(builder.Configuration, readiness)
    .AddCacheStore(builder.Configuration, readiness)
    .AddEventBus(builder.Configuration); // also the consumer group positions used by the rebuild

// Availability read model (UC-03, RF-03, Phase 4, ADR-0008): rebuild, initial load and the two projectors.
builder.Services.AddSingleton<ReadModelMetrics>();
builder.Services.AddSingleton<ReadModelGate>();
builder.Services.AddScoped<ReadModelRebuilder>();

if (!rebuildOnly)
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

    // RF-06 → read model: configuration events relayed from the outbox (relay in Phase 5).
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
            var report = await scope.ServiceProvider.GetRequiredService<ReadModelRebuilder>().RebuildAsync(cancel.Token);
            Console.WriteLine(
                $"Read model rebuilt: {report.Facilities} facilities, {report.Spots} spots, " +
                $"{report.Events} latest events ({report.EventsApplied} applied) in {report.Duration.TotalMilliseconds:F0} ms.");
        }
        catch (RebuildInProgressException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Environment.ExitCode = 1;
        }
    }

    return;
}

app.MapDefaultEndpoints();

await app.RunAsync();
