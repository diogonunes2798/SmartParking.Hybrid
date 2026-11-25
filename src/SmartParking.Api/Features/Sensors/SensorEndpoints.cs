using SmartParking.Api.Common;
using SmartParking.Application.Abstractions.Messaging;
using SmartParking.Application.Abstractions.Paging;
using SmartParking.Application.Abstractions.Persistence;
using SmartParking.Application.Abstractions.Reading;
using SmartParking.Application.Features.Sensors;
using SmartParking.Domain.Facilities;
using SmartParking.Infrastructure.Hosting;

namespace SmartParking.Api.Features.Sensors;

/// <summary>
/// Sensor registry (RF-06, UC-05; spec 001). The spot is changed through <c>/{id}/spot</c> (AC-14). Reads carry the
/// <c>xmin</c> as a strong ETag; every change needs it in <c>If-Match</c>. Anonymous on purpose until F11 (RBAC).
/// </summary>
internal sealed class SensorEndpoints : IEndpointGroup
{
    private const string GetSensorRoute = "GetSensor";

    public void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/sensors")
            .WithTags("Sensors")
            .AllowAnonymous();

        group.MapPost("/", RegisterAsync)
            .WithRequestValidation<RegisterSensorRequest>()
            .Produces<SensorResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapGet("/", ListAsync)
            .WithRequestValidation<PageRequest>()
            .WithRequestValidation<SensorFilterRequest>()
            .Produces<PagedResponse<SensorResponse>>();

        group.MapGet("/{id:guid}", GetAsync)
            .WithName(GetSensorRoute)
            .Produces<SensorResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPut("/{id:guid}", UpdateAsync)
            .WithRequestValidation<UpdateSensorRequest>()
            .Produces<SensorResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        group.MapPut("/{id:guid}/spot", AssignAsync)
            .WithRequestValidation<AssignSensorRequest>()
            .Produces<SensorResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        group.MapDelete("/{id:guid}/spot", UnassignAsync)
            .Produces<SensorResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        group.MapDelete("/{id:guid}", RemoveAsync)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);
    }

    private static async Task<IResult> RegisterAsync(
        RegisterSensorRequest request,
        ICommandHandler<RegisterSensor.Command, SensorView> handler,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(request.ToCommand(), cancellationToken);
        if (result.Error is { } error)
        {
            return error.ToProblem();
        }

        response.SetETag(result.Value.Version);
        return TypedResults.CreatedAtRoute(SensorResponse.From(result.Value), GetSensorRoute, new { id = result.Value.Id });
    }

    private static async Task<IResult> ListAsync(
        [AsParameters] PageRequest page,
        [AsParameters] SensorFilterRequest filter,
        IQueryHandler<ListSensors.Query, PagedResult<SensorView>> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new ListSensors.Query(filter.ToFilter(), page), cancellationToken);
        return result.Error is { } error
            ? error.ToProblem()
            : TypedResults.Ok(new PagedResponse<SensorResponse>(
                [.. result.Value.Items.Select(SensorResponse.From)], result.Value.Page, result.Value.PageSize, result.Value.TotalCount));
    }

    private static async Task<IResult> GetAsync(
        Guid id,
        IQueryHandler<GetSensor.Query, SensorView> handler,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new GetSensor.Query(id), cancellationToken);
        return Respond(result.Error, result.IsSuccess ? result.Value : null, response);
    }

    private static async Task<IResult> UpdateAsync(
        Guid id,
        UpdateSensorRequest request,
        ICommandHandler<UpdateSensor.Command, SensorView> handler,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (!context.Request.TryGetIfMatch(out var expectedVersion))
        {
            return ConcurrencyCheck.StaleVersion.ToProblem();
        }

        var command = new UpdateSensor.Command(
            id, expectedVersion, request.Model!, Enum.Parse<SensorStatus>(request.Status!), request.ZoneId);
        var result = await handler.HandleAsync(command, cancellationToken);
        return Respond(result.Error, result.IsSuccess ? result.Value : null, context.Response);
    }

    private static async Task<IResult> AssignAsync(
        Guid id,
        AssignSensorRequest request,
        ICommandHandler<AssignSensorToSpot.Command, SensorView> handler,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (!context.Request.TryGetIfMatch(out var expectedVersion))
        {
            return ConcurrencyCheck.StaleVersion.ToProblem();
        }

        var result = await handler.HandleAsync(new AssignSensorToSpot.Command(id, expectedVersion, request.SpotId), cancellationToken);
        return Respond(result.Error, result.IsSuccess ? result.Value : null, context.Response);
    }

    private static async Task<IResult> UnassignAsync(
        Guid id,
        ICommandHandler<UnassignSensor.Command, SensorView> handler,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (!context.Request.TryGetIfMatch(out var expectedVersion))
        {
            return ConcurrencyCheck.StaleVersion.ToProblem();
        }

        var result = await handler.HandleAsync(new UnassignSensor.Command(id, expectedVersion), cancellationToken);
        return Respond(result.Error, result.IsSuccess ? result.Value : null, context.Response);
    }

    private static async Task<IResult> RemoveAsync(
        Guid id,
        ICommandHandler<RemoveSensor.Command, Guid> handler,
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        if (!request.TryGetIfMatch(out var expectedVersion))
        {
            return ConcurrencyCheck.StaleVersion.ToProblem();
        }

        var result = await handler.HandleAsync(new RemoveSensor.Command(id, expectedVersion), cancellationToken);
        return result.Error is { } error ? error.ToProblem() : TypedResults.NoContent();
    }

    private static IResult Respond(Application.Abstractions.Error? error, SensorView? view, HttpResponse response)
    {
        if (error is not null)
        {
            return error.ToProblem();
        }

        response.SetETag(view!.Version);
        return TypedResults.Ok(SensorResponse.From(view));
    }
}
