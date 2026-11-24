using SmartParking.Application;
using SmartParking.Infrastructure;
using SmartParking.Infrastructure.Hosting;

var builder = WebApplication.CreateBuilder(args);

builder.AddHostingDefaults("SmartParking.Workers");
builder.Services
    .AddApplication()
    .AddInfrastructure(builder.Configuration)
    .AddEndpointGroups(typeof(Program).Assembly);

var app = builder.Build();

app.UseHostingDefaults();
app.MapEndpoints();

await app.RunAsync();
