using SmartParking.Api.Common;
using SmartParking.Application.Abstractions.Messaging;
using SmartParking.Application.Abstractions.Paging;
using SmartParking.Application.Abstractions.Persistence;
using SmartParking.Application.Abstractions.Reading;
using SmartParking.Application.Features.Spots;
using SmartParking.Domain.Facilities;
using SmartParking.Infrastructure.Hosting;

namespace SmartParking.Api.Features.Spots;

/// <summary>
/// Parking spots of a facility, one by one or in batches of up to 1 000 (RF-06, spec 001). Reads carry the
/// <c>xmin</c> as a strong ETag; PUT and DELETE need it in <c>If-Match</c>. Anonymous on purpose until F11 (RBAC).
/// </summary>
internal sealed class SpotEndpoints : IEndpointGroup
{
    private const string GetSpotRoute = "GetSpot";

    public void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/facilities/{facilityId:guid}/spots")
            .WithTags("Spots")
            .AllowAnonymous();

        group.MapPost("/", CreateAsync)
            .WithRequestValidation<CreateSpotRequest>()
            .Produces<SpotResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/batch", CreateBatchAsync)
            .WithRequestValidation<SpotBatchRequest>()
            .Produces<SpotBatchResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapGet("/", ListAsync)
            .WithRequestValidation<PageRequest>()
            .WithRequestValidation<SpotFilterRequest>()
            .Produces<PagedResponse<SpotResponse>>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/{spotId:guid}", GetAsync)
            .WithName(GetSpotRoute)
            .Produces<SpotResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPut("/{spotId:guid}", UpdateAsync)
            .WithRequestValidation<UpdateSpotRequest>()
            .Produces<SpotResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        group.MapDelete("/{spotId:guid}", DeleteAsync)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);
    }

    private static async Task<IResult> CreateAsync(
        Guid facilityId,
        CreateSpotRequest request,
        ICommandHandler<CreateSpot.Command, SpotView> handler,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        var command = new CreateSpot.Command(facilityId, request.ZoneId, request.Code!, Enum.Parse<SpotType>(request.Type!));
        var result = await handler.HandleAsync(command, cancellationToken);
        if (result.Error is { } error)
        {
            return error.ToProblem();
        }

        response.SetETag(result.Value.Version);
        return TypedResults.CreatedAtRoute(
            SpotResponse.From(result.Value), GetSpotRoute, new { facilityId, spotId = result.Value.Id });
    }

    private static async Task<IResult> CreateBatchAsync(
        Guid facilityId,
        SpotBatchRequest request,
        ICommandHandler<CreateSpotBatch.Command, IReadOnlyList<CreateSpotBatch.CreatedItem>> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(request.ToCommand(facilityId), cancellationToken);
        if (result.Error is { } error)
        {
            return error.ToProblem();
        }

        // A batch is not an addressable resource: 201 without Location (plan 001b, point 7).
        return TypedResults.Created(
            (string?)null,
            new SpotBatchResponse([.. result.Value.Select(item => new SpotBatchItemResponse(item.SpotId, item.Code, item.SensorId))]));
    }

    private static async Task<IResult> ListAsync(
        Guid facilityId,
        [AsParameters] PageRequest page,
        [AsParameters] SpotFilterRequest filter,
        IQueryHandler<ListSpots.Query, PagedResult<SpotView>> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new ListSpots.Query(facilityId, filter.ToFilter(), page), cancellationToken);
        return result.Error is { } error
            ? error.ToProblem()
            : TypedResults.Ok(new PagedResponse<SpotResponse>(
                [.. result.Value.Items.Select(SpotResponse.From)], result.Value.Page, result.Value.PageSize, result.Value.TotalCount));
    }

    private static async Task<IResult> GetAsync(
        Guid facilityId,
        Guid spotId,
        IQueryHandler<GetSpot.Query, SpotView> handler,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new GetSpot.Query(facilityId, spotId), cancellationToken);
        if (result.Error is { } error)
        {
            return error.ToProblem();
        }

        response.SetETag(result.Value.Version);
        return TypedResults.Ok(SpotResponse.From(result.Value));
    }

    private static async Task<IResult> UpdateAsync(
        Guid facilityId,
        Guid spotId,
        UpdateSpotRequest request,
        ICommandHandler<UpdateSpot.Command, SpotView> handler,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (!context.Request.TryGetIfMatch(out var expectedVersion))
        {
            return ConcurrencyCheck.StaleVersion.ToProblem();
        }

        var command = new UpdateSpot.Command(
            facilityId, spotId, expectedVersion, request.ZoneId, request.Code!,
            Enum.Parse<SpotType>(request.Type!), Enum.Parse<SpotStatus>(request.Status!));
        var result = await handler.HandleAsync(command, cancellationToken);
        if (result.Error is { } error)
        {
            return error.ToProblem();
        }

        context.Response.SetETag(result.Value.Version);
        return TypedResults.Ok(SpotResponse.From(result.Value));
    }

    private static async Task<IResult> DeleteAsync(
        Guid facilityId,
        Guid spotId,
        ICommandHandler<DeleteSpot.Command, Guid> handler,
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        if (!request.TryGetIfMatch(out var expectedVersion))
        {
            return ConcurrencyCheck.StaleVersion.ToProblem();
        }

        var result = await handler.HandleAsync(new DeleteSpot.Command(facilityId, spotId, expectedVersion), cancellationToken);
        return result.Error is { } error ? error.ToProblem() : TypedResults.NoContent();
    }
}
