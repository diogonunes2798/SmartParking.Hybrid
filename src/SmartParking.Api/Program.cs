using Scalar.AspNetCore;
using SmartParking.Application;
using SmartParking.Infrastructure;
using SmartParking.Infrastructure.Hosting;

var builder = WebApplication.CreateBuilder(args);

builder.AddHostingDefaults("SmartParking.Api");
builder.Services
    .AddApplication()
    .AddInfrastructure(builder.Configuration)
    .AddEndpointGroups(typeof(Program).Assembly)
    .AddOpenApi();

var app = builder.Build();

app.UseHostingDefaults();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.MapEndpoints();

await app.RunAsync();
