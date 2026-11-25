using SmartParking.Application;
using SmartParking.Application.Abstractions;
using SmartParking.Infrastructure;
using SmartParking.Infrastructure.Hosting;
using SmartParking.Workers.Hosting;
using SmartParking.Workers.Reservations;

var builder = WebApplication.CreateBuilder(args);

builder.AddHostingDefaults("SmartParking.Workers");

// Before AddInfrastructure, which only adds the anonymous actor when none is registered (spec 002, RNF-06).
builder.Services.AddSingleton<ICurrentActor, SystemCurrentActor>();
builder.Services
    .AddApplication()
    .AddInfrastructure(builder.Configuration)
    .AddEndpointGroups(typeof(Program).Assembly)
    .AddReservationExpiry();

var app = builder.Build();

app.UseHostingDefaults();
app.MapEndpoints();

await app.RunAsync();
