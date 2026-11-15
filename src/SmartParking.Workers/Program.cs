using SmartParking.Application.Ingestion;
using SmartParking.Infrastructure.Cache;
using SmartParking.Infrastructure.Documents;
using SmartParking.Infrastructure.Messaging;
using SmartParking.Infrastructure.Relational;
using SmartParking.ServiceDefaults;

// The workers host is a (minimal) web host so that it exposes /health and /metrics like the API:
// readiness and metrics of the consumers are needed for RNF-02/RNF-03 measurements.
var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults("smartparking-workers");

var readiness = ServiceDefaultsExtensions.ReadinessTags;
builder.Services
    .AddRelationalPersistence(builder.Configuration, readiness)
    .AddDocumentPersistence(builder.Configuration, readiness)
    .AddCacheStore(builder.Configuration, readiness);

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

var app = builder.Build();

app.MapDefaultEndpoints();

await app.RunAsync();
