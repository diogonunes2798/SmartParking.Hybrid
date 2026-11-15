using System.Text.Json.Serialization;
using SmartParking.Api;
using SmartParking.Api.Endpoints;
using SmartParking.Infrastructure.Cache;
using SmartParking.Infrastructure.Documents;
using SmartParking.Infrastructure.Messaging;
using SmartParking.Infrastructure.Relational;
using SmartParking.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults("smartparking-api");

builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ProblemDetailsExceptionHandler>();

// Enums travel by name ("Covered", "Car"), as they are stored (readable and robust to reordering).
// Required constructor parameters must be present in the body: a missing "validFrom" or "version" is a 400,
// not a silent default value (review of Phase 2a, finding 3). Optional fields declare a default (e.g. validTo).
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    options.SerializerOptions.RespectRequiredConstructorParameters = true;
});

// Minimal APIs throw BadHttpRequestException (unreadable or missing body) only in Development by default;
// elsewhere they answer an empty 400. Throwing always lets ProblemDetailsExceptionHandler produce the same
// RFC 9457 response in every environment (review of Phase 2a, finding 1).
builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);

var readiness = ServiceDefaultsExtensions.ReadinessTags;
builder.Services
    .AddApplicationHandlers(builder.Configuration)
    .AddRelationalPersistence(builder.Configuration, readiness)
    .AddDocumentPersistence(builder.Configuration, readiness)
    .AddCacheStore(builder.Configuration, readiness)
    .AddEventBus(builder.Configuration);

var app = builder.Build();

app.UseExceptionHandler();

// OpenAPI/Swagger only in Development and in the integration-test environment ("Testing").
if (app.Environment.IsDevelopment() || app.Environment.IsEnvironment("Testing"))
{
    app.MapOpenApi();
    app.UseSwaggerUI(options => options.SwaggerEndpoint("/openapi/v1.json", "SmartParking API v1"));
}

app.MapDefaultEndpoints();
app.MapConfigurationEndpoints();
app.MapSessionEndpoints();
app.MapIngestionEndpoints();
app.MapAvailabilityEndpoints();

await app.RunAsync();

/// <summary>Exposed for WebApplicationFactory in the integration tests.</summary>
public partial class Program;
