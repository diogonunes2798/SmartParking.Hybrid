using SmartParking.Api.Common;
using SmartParking.Application.Abstractions.Messaging;
using SmartParking.Application.Abstractions.Paging;
using SmartParking.Application.Abstractions.Persistence;
using SmartParking.Application.Abstractions.Reading;
using SmartParking.Application.Features.Tariffs;
using SmartParking.Infrastructure.Hosting;

namespace SmartParking.Api.Features.Tariffs;

/// <summary>
/// Tariff versions of a facility (RF-06, UC-02; spec 001, ADR 0005). There is no PUT: a version that has started is
/// immutable, and a change is a new version. Only the latest future version can be deleted (If-Match required).
/// Anonymous on purpose until F11 (RBAC).
/// </summary>
internal sealed class TariffEndpoints : IEndpointGroup
{
    private const string GetTariffRoute = "GetTariffVersion";

    public void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/facilities/{facilityId:guid}/tariffs")
            .WithTags("Tariffs")
            .AllowAnonymous();

        group.MapPost("/", CreateAsync)
            .WithRequestValidation<CreateTariffRequest>()
            .Produces<TariffResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapGet("/", ListAsync)
            .WithRequestValidation<PageRequest>()
            .Produces<PagedResponse<TariffResponse>>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/current", GetCurrentAsync)
            .Produces<TariffResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/{tariffId:guid}", GetAsync)
            .WithName(GetTariffRoute)
            .Produces<TariffResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapDelete("/{tariffId:guid}", DeleteAsync)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);
    }

    private static async Task<IResult> CreateAsync(
        Guid facilityId,
        CreateTariffRequest request,
        ICommandHandler<CreateTariffVersion.Command, TariffView> handler,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(request.ToCommand(facilityId), cancellationToken);
        if (result.Error is { } error)
        {
            return error.ToProblem();
        }

        response.SetETag(result.Value.Version);
        return TypedResults.CreatedAtRoute(
            TariffResponse.From(result.Value), GetTariffRoute, new { facilityId, tariffId = result.Value.Id });
    }

    private static async Task<IResult> ListAsync(
        Guid facilityId,
        [AsParameters] PageRequest page,
        IQueryHandler<ListTariffVersions.Query, PagedResult<TariffView>> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new ListTariffVersions.Query(facilityId, page), cancellationToken);
        return result.Error is { } error
            ? error.ToProblem()
            : TypedResults.Ok(new PagedResponse<TariffResponse>(
                [.. result.Value.Items.Select(TariffResponse.From)], result.Value.Page, result.Value.PageSize, result.Value.TotalCount));
    }

    private static async Task<IResult> GetCurrentAsync(
        Guid facilityId,
        IQueryHandler<GetCurrentTariff.Query, TariffView> handler,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new GetCurrentTariff.Query(facilityId), cancellationToken);
        if (result.Error is { } error)
        {
            return error.ToProblem();
        }

        response.SetETag(result.Value.Version);
        return TypedResults.Ok(TariffResponse.From(result.Value));
    }

    private static async Task<IResult> GetAsync(
        Guid facilityId,
        Guid tariffId,
        IQueryHandler<GetTariffVersion.Query, TariffView> handler,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new GetTariffVersion.Query(facilityId, tariffId), cancellationToken);
        if (result.Error is { } error)
        {
            return error.ToProblem();
        }

        response.SetETag(result.Value.Version);
        return TypedResults.Ok(TariffResponse.From(result.Value));
    }

    private static async Task<IResult> DeleteAsync(
        Guid facilityId,
        Guid tariffId,
        ICommandHandler<DeleteTariffVersion.Command, Guid> handler,
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        if (!request.TryGetIfMatch(out var expectedVersion))
        {
            return ConcurrencyCheck.StaleVersion.ToProblem();
        }

        var result = await handler.HandleAsync(
            new DeleteTariffVersion.Command(facilityId, tariffId, expectedVersion), cancellationToken);
        return result.Error is { } error ? error.ToProblem() : TypedResults.NoContent();
    }
}
