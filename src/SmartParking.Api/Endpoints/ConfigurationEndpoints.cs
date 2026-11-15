using Microsoft.AspNetCore.Http.HttpResults;
using SmartParking.Application.Configuration;

namespace SmartParking.Api.Endpoints;

/// <summary>
/// RF-06: CRUD of facilities, spots, sensors and tariffs. Commands go to the Application command handlers,
/// reads to <see cref="IConfigurationQueries"/> (CQRS). Errors are turned into ProblemDetails by
/// <see cref="ProblemDetailsExceptionHandler"/>: validation 400, missing 404, conflicts 409.
/// </summary>
internal static class ConfigurationEndpoints
{
    public static IEndpointRouteBuilder MapConfigurationEndpoints(this IEndpointRouteBuilder app)
    {
        MapFacilities(app.MapGroup("/api/facilities").WithTags("Facilities"));
        MapSpots(app.MapGroup("/api").WithTags("Spots"));
        MapSensors(app.MapGroup("/api/sensors").WithTags("Sensors"));
        MapTariffs(app.MapGroup("/api/tariffs").WithTags("Tariffs"));
        return app;
    }

    private static void MapFacilities(RouteGroupBuilder group)
    {
        group.MapGet("/", (int? skip, int? take, IConfigurationQueries queries, CancellationToken ct) =>
            queries.ListFacilitiesAsync(Page(skip, take), ct));

        group.MapGet("/{id:guid}", async Task<Results<Ok<FacilityDto>, NotFound>> (
            Guid id, IConfigurationQueries queries, CancellationToken ct) =>
            await queries.GetFacilityAsync(id, ct) is { } dto ? TypedResults.Ok(dto) : TypedResults.NotFound());

        group.MapPost("/", async (CreateFacility command, FacilityCommands handler, CancellationToken ct) =>
        {
            var dto = await handler.CreateAsync(command, ct);
            return TypedResults.Created($"/api/facilities/{dto.Id}", dto);
        });

        group.MapPut("/{id:guid}", (Guid id, UpdateFacility command, FacilityCommands handler, CancellationToken ct) =>
            handler.UpdateAsync(id, command, ct));

        group.MapDelete("/{id:guid}", async (Guid id, FacilityCommands handler, CancellationToken ct) =>
        {
            await handler.DeleteAsync(id, ct);
            return TypedResults.NoContent();
        });
    }

    private static void MapSpots(RouteGroupBuilder group)
    {
        group.MapGet("/facilities/{facilityId:guid}/spots", (
            Guid facilityId, int? skip, int? take, IConfigurationQueries queries, CancellationToken ct) =>
            queries.ListSpotsAsync(facilityId, Page(skip, take), ct));

        group.MapPost("/facilities/{facilityId:guid}/spots", async (
            Guid facilityId, CreateSpot command, SpotCommands handler, CancellationToken ct) =>
        {
            var dto = await handler.CreateAsync(facilityId, command, ct);
            return TypedResults.Created($"/api/spots/{dto.Id}", dto);
        });

        group.MapGet("/spots/{id:guid}", async Task<Results<Ok<SpotDto>, NotFound>> (
            Guid id, IConfigurationQueries queries, CancellationToken ct) =>
            await queries.GetSpotAsync(id, ct) is { } dto ? TypedResults.Ok(dto) : TypedResults.NotFound());

        group.MapPut("/spots/{id:guid}", (Guid id, UpdateSpot command, SpotCommands handler, CancellationToken ct) =>
            handler.UpdateAsync(id, command, ct));

        group.MapDelete("/spots/{id:guid}", async (Guid id, SpotCommands handler, CancellationToken ct) =>
        {
            await handler.DeleteAsync(id, ct);
            return TypedResults.NoContent();
        });
    }

    private static void MapSensors(RouteGroupBuilder group)
    {
        group.MapGet("/", (Guid? spotId, int? skip, int? take, IConfigurationQueries queries, CancellationToken ct) =>
            queries.ListSensorsAsync(spotId, Page(skip, take), ct));

        group.MapGet("/{id:guid}", async Task<Results<Ok<SensorDto>, NotFound>> (
            Guid id, IConfigurationQueries queries, CancellationToken ct) =>
            await queries.GetSensorAsync(id, ct) is { } dto ? TypedResults.Ok(dto) : TypedResults.NotFound());

        group.MapPost("/", async (CreateSensor command, SensorCommands handler, CancellationToken ct) =>
        {
            var dto = await handler.CreateAsync(command, ct);
            return TypedResults.Created($"/api/sensors/{dto.Id}", dto);
        });

        group.MapPut("/{id:guid}", (Guid id, UpdateSensor command, SensorCommands handler, CancellationToken ct) =>
            handler.UpdateAsync(id, command, ct));

        group.MapDelete("/{id:guid}", async (Guid id, SensorCommands handler, CancellationToken ct) =>
        {
            await handler.DeleteAsync(id, ct);
            return TypedResults.NoContent();
        });
    }

    private static void MapTariffs(RouteGroupBuilder group)
    {
        group.MapGet("/", (string? zone, int? skip, int? take, IConfigurationQueries queries, CancellationToken ct) =>
            queries.ListTariffsAsync(zone, Page(skip, take), ct));

        group.MapGet("/{id:guid}", async Task<Results<Ok<TariffDto>, NotFound>> (
            Guid id, IConfigurationQueries queries, CancellationToken ct) =>
            await queries.GetTariffAsync(id, ct) is { } dto ? TypedResults.Ok(dto) : TypedResults.NotFound());

        group.MapPost("/", async (CreateTariff command, TariffCommands handler, CancellationToken ct) =>
        {
            var dto = await handler.CreateAsync(command, ct);
            return TypedResults.Created($"/api/tariffs/{dto.Id}", dto);
        });

        group.MapPut("/{id:guid}", (Guid id, UpdateTariff command, TariffCommands handler, CancellationToken ct) =>
            handler.UpdateAsync(id, command, ct));

        group.MapDelete("/{id:guid}", async (Guid id, TariffCommands handler, CancellationToken ct) =>
        {
            await handler.DeleteAsync(id, ct);
            return TypedResults.NoContent();
        });
    }

    private static PageRequest Page(int? skip, int? take) =>
        new PageRequest(skip ?? 0, take ?? PageRequest.DefaultTake).Validated();
}
