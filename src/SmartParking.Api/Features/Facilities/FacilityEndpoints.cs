using SmartParking.Api.Common;
using SmartParking.Application.Abstractions.Messaging;
using SmartParking.Application.Abstractions.Paging;
using SmartParking.Application.Abstractions.Persistence;
using SmartParking.Application.Abstractions.Reading;
using SmartParking.Application.Features.Facilities;
using SmartParking.Infrastructure.Hosting;

namespace SmartParking.Api.Features.Facilities;

/// <summary>
/// Parking facility configuration (RF-06, spec 001). Reads carry the <c>xmin</c> as a strong ETag; PUT and DELETE
/// need it in <c>If-Match</c> (428 without it, 412 when stale). Anonymous on purpose until F11 (RBAC).
/// </summary>
internal sealed class FacilityEndpoints : IEndpointGroup
{
    private const string GetFacilityRoute = "GetFacility";

    public void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/facilities")
            .WithTags("Facilities")
            .AllowAnonymous();

        group.MapPost("/", CreateAsync)
            .WithRequestValidation<CreateFacilityRequest>()
            .Produces<FacilityResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapGet("/", ListAsync)
            .WithRequestValidation<PageRequest>()
            .Produces<PagedResponse<FacilityResponse>>();

        group.MapGet("/{id:guid}", GetAsync)
            .WithName(GetFacilityRoute)
            .Produces<FacilityResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPut("/{id:guid}", UpdateAsync)
            .WithRequestValidation<UpdateFacilityRequest>()
            .Produces<FacilityResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        group.MapDelete("/{id:guid}", DeleteAsync)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);
    }

    private static async Task<IResult> CreateAsync(
        CreateFacilityRequest request,
        ICommandHandler<CreateFacility.Command, FacilityView> handler,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(request.ToCommand(), cancellationToken);
        if (result.Error is { } error)
        {
            return error.ToProblem();
        }

        response.SetETag(result.Value.Version);
        return TypedResults.CreatedAtRoute(result.Value.ToResponse(), GetFacilityRoute, new { id = result.Value.Id });
    }

    private static async Task<IResult> ListAsync(
        [AsParameters] PageRequest page,
        IQueryHandler<ListFacilities.Query, PagedResult<FacilityView>> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new ListFacilities.Query(page), cancellationToken);
        return result.Error is { } error ? error.ToProblem() : TypedResults.Ok(result.Value.ToResponse());
    }

    private static async Task<IResult> GetAsync(
        Guid id,
        IQueryHandler<GetFacility.Query, FacilityView> handler,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new GetFacility.Query(id), cancellationToken);
        if (result.Error is { } error)
        {
            return error.ToProblem();
        }

        response.SetETag(result.Value.Version);
        return TypedResults.Ok(result.Value.ToResponse());
    }

    private static async Task<IResult> UpdateAsync(
        Guid id,
        UpdateFacilityRequest request,
        ICommandHandler<UpdateFacility.Command, FacilityView> handler,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (!context.Request.TryGetIfMatch(out var expectedVersion))
        {
            return ConcurrencyCheck.StaleVersion.ToProblem();
        }

        var result = await handler.HandleAsync(request.ToCommand(id, expectedVersion), cancellationToken);
        if (result.Error is { } error)
        {
            return error.ToProblem();
        }

        context.Response.SetETag(result.Value.Version);
        return TypedResults.Ok(result.Value.ToResponse());
    }

    private static async Task<IResult> DeleteAsync(
        Guid id,
        ICommandHandler<DeleteFacility.Command, Guid> handler,
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        if (!request.TryGetIfMatch(out var expectedVersion))
        {
            return ConcurrencyCheck.StaleVersion.ToProblem();
        }

        var result = await handler.HandleAsync(new DeleteFacility.Command(id, expectedVersion), cancellationToken);
        return result.Error is { } error ? error.ToProblem() : TypedResults.NoContent();
    }
}
