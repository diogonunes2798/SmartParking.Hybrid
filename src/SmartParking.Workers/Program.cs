using SmartParking.Infrastructure.Cache;
using SmartParking.Infrastructure.Documents;
using SmartParking.Infrastructure.Relational;
using SmartParking.ServiceDefaults;

// The workers host is a (minimal) web host so that it exposes /health and /metrics like the API:
// readiness and metrics of the consumers are needed for RNF-02/RNF-03 measurements.
// Projections, outbox dispatcher and retention jobs (BackgroundService) arrive in later phases.
var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults("smartparking-workers");

var readiness = ServiceDefaultsExtensions.ReadinessTags;
builder.Services
    .AddRelationalPersistence(builder.Configuration, readiness)
    .AddDocumentPersistence(builder.Configuration, readiness)
    .AddCacheStore(builder.Configuration, readiness);

var app = builder.Build();

app.MapDefaultEndpoints();

await app.RunAsync();
