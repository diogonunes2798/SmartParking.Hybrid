using Microsoft.AspNetCore.Http.HttpResults;
using SmartParking.Application.Sessions;
using SmartParking.Application.Users;

namespace SmartParking.Api.Endpoints;

/// <summary>
/// UC-02 (sessions and payment) and the minimal creation of users and vehicles it needs. Commands go to
/// <see cref="SessionCommands"/> and <see cref="UserCommands"/>, reads to <see cref="ISessionQueries"/> (CQRS).
/// Each command is one transaction with its outbox rows (ADR-0004). Conflicts (concurrency, invalid state
/// transition, spot already occupied, no tariff in force) are 409 via <see cref="ProblemDetailsExceptionHandler"/>.
/// </summary>
internal static class SessionEndpoints
{
    public static IEndpointRouteBuilder MapSessionEndpoints(this IEndpointRouteBuilder app)
    {
        MapSessions(app.MapGroup("/api/sessions").WithTags("Sessions"));
        MapUsers(app.MapGroup("/api/users").WithTags("Users"));
        return app;
    }

    private static void MapSessions(RouteGroupBuilder group)
    {
        group.MapPost("/", async (StartSession command, SessionCommands handler, CancellationToken ct) =>
        {
            var dto = await handler.StartAsync(command, ct);
            return TypedResults.Created($"/api/sessions/{dto.Id}", dto);
        });

        group.MapGet("/{id:guid}", async Task<Results<Ok<SessionDto>, NotFound>> (
            Guid id, ISessionQueries queries, CancellationToken ct) =>
            await queries.GetSessionAsync(id, ct) is { } dto ? TypedResults.Ok(dto) : TypedResults.NotFound());

        // The body is optional: an empty POST closes without a version check.
        group.MapPost("/{id:guid}/close", (Guid id, CloseSession? command, SessionCommands handler, CancellationToken ct) =>
            handler.CloseAsync(id, command ?? new CloseSession(), ct));

        group.MapPost("/{id:guid}/pay", async (Guid id, PaySession command, SessionCommands handler, CancellationToken ct) =>
        {
            var dto = await handler.PayAsync(id, command, ct);
            return TypedResults.Created($"/api/sessions/{id}", dto);
        });
    }

    private static void MapUsers(RouteGroupBuilder group)
    {
        group.MapPost("/", async (CreateUser command, UserCommands handler, CancellationToken ct) =>
        {
            var dto = await handler.CreateUserAsync(command, ct);
            return TypedResults.Created($"/api/users/{dto.Id}", dto);
        });

        group.MapPost("/{userId:guid}/vehicles", async (
            Guid userId, CreateVehicle command, UserCommands handler, CancellationToken ct) =>
        {
            var dto = await handler.CreateVehicleAsync(userId, command, ct);
            return TypedResults.Created($"/api/users/{userId}/vehicles/{dto.Id}", dto);
        });
    }
}
