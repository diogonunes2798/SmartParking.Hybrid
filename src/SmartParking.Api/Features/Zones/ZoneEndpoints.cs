using SmartParking.Api.Common;
using SmartParking.Application.Abstractions.Messaging;
using SmartParking.Application.Abstractions.Paging;
using SmartParking.Application.Abstractions.Persistence;
using SmartParking.Application.Abstractions.Reading;
using SmartParking.Application.Features.Zones;
using SmartParking.Infrastructure.Hosting;

namespace SmartParking.Api.Features.Zones;

/// <summary>
/// Zones of a parking facility (RF-06, spec 001). Reads carry the <c>xmin</c> as a strong ETag; PUT and DELETE need it
/// in <c>If-Match</c> (428 without it, 412 when stale or malformed). Anonymous on purpose until F11 (RBAC).
/// </summary>
internal sealed class ZoneEndpoints : IEndpointGroup
{
    private const string GetZoneRoute = "GetZone";

    public void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/facilities/{facilityId:guid}/zones")
            .WithTags("Zones")
            .AllowAnonymous();

        group.MapPost("/", CreateAsync)
            .WithRequestValidation<ZoneRequest>()
            .Produces<ZoneResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapGet("/", ListAsync)
            .WithRequestValidation<PageRequest>()
            .Produces<PagedResponse<ZoneResponse>>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/{zoneId:guid}", GetAsync)
            .WithName(GetZoneRoute)
            .Produces<ZoneResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPut("/{zoneId:guid}", UpdateAsync)
            .WithRequestValidation<ZoneRequest>()
            .Produces<ZoneResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        group.MapDelete("/{zoneId:guid}", DeleteAsync)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);
    }

    private static async Task<IResult> CreateAsync(
        Guid facilityId,
        ZoneRequest request,
        ICommandHandler<CreateZone.Command, ZoneView> handler,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new CreateZone.Command(facilityId, request.Name!), cancellationToken);
        if (result.Error is { } error)
        {
            return error.ToProblem();
        }

        response.SetETag(result.Value.Version);
        return TypedResults.CreatedAtRoute(
            ZoneResponse.From(result.Value), GetZoneRoute, new { facilityId, zoneId = result.Value.Id });
    }

    private static async Task<IResult> ListAsync(
        Guid facilityId,
        [AsParameters] PageRequest page,
        IQueryHandler<ListZones.Query, PagedResult<ZoneView>> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new ListZones.Query(facilityId, page), cancellationToken);
        return result.Error is { } error
            ? error.ToProblem()
            : TypedResults.Ok(new PagedResponse<ZoneResponse>(
                [.. result.Value.Items.Select(ZoneResponse.From)], result.Value.Page, result.Value.PageSize, result.Value.TotalCount));
    }

    private static async Task<IResult> GetAsync(
        Guid facilityId,
        Guid zoneId,
        IQueryHandler<GetZone.Query, ZoneView> handler,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new GetZone.Query(facilityId, zoneId), cancellationToken);
        if (result.Error is { } error)
        {
            return error.ToProblem();
        }

        response.SetETag(result.Value.Version);
        return TypedResults.Ok(ZoneResponse.From(result.Value));
    }

    private static async Task<IResult> UpdateAsync(
        Guid facilityId,
        Guid zoneId,
        ZoneRequest request,
        ICommandHandler<UpdateZone.Command, ZoneView> handler,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (!context.Request.TryGetIfMatch(out var expectedVersion))
        {
            return ConcurrencyCheck.StaleVersion.ToProblem();
        }

        var result = await handler.HandleAsync(
            new UpdateZone.Command(facilityId, zoneId, expectedVersion, request.Name!), cancellationToken);
        if (result.Error is { } error)
        {
            return error.ToProblem();
        }

        context.Response.SetETag(result.Value.Version);
        return TypedResults.Ok(ZoneResponse.From(result.Value));
    }

    private static async Task<IResult> DeleteAsync(
        Guid facilityId,
        Guid zoneId,
        ICommandHandler<DeleteZone.Command, Guid> handler,
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        if (!request.TryGetIfMatch(out var expectedVersion))
        {
            return ConcurrencyCheck.StaleVersion.ToProblem();
        }

        var result = await handler.HandleAsync(new DeleteZone.Command(facilityId, zoneId, expectedVersion), cancellationToken);
        return result.Error is { } error ? error.ToProblem() : TypedResults.NoContent();
    }
}
