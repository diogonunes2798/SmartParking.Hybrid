using SmartParking.Api.Common;
using SmartParking.Application.Abstractions.Messaging;
using SmartParking.Application.Abstractions.Reading;
using SmartParking.Application.Features.Reservations;
using SmartParking.Infrastructure.Hosting;

namespace SmartParking.Api.Features.Reservations;

/// <summary>
/// Reservations: create, read and cancel (UC-02, RF-02, spec 002). Fulfilment is opening a session with the
/// <c>reservationId</c>; expiry is the Workers' job. No <c>If-Match</c> and no <c>ETag</c>: a stale <c>xmin</c> on
/// cancel gives 409. Anonymous on purpose until F11 (authentication and RBAC).
/// </summary>
internal sealed class ReservationEndpoints : IEndpointGroup
{
    private const string GetReservationRoute = "GetReservation";

    public void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/reservations")
            .WithTags("Reservations")
            .AllowAnonymous();

        group.MapPost("/", CreateAsync)
            .WithRequestValidation<CreateReservationRequest>()
            .Produces<ReservationResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapGet("/{id:guid}", GetAsync)
            .WithName(GetReservationRoute)
            .Produces<ReservationResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{id:guid}/cancel", CancelAsync)
            .Produces<ReservationResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }

    private static async Task<IResult> CreateAsync(
        CreateReservationRequest request,
        ICommandHandler<CreateReservation.Command, ReservationView> handler,
        CancellationToken cancellationToken)
    {
        var command = new CreateReservation.Command(
            request.SpotId!.Value, request.UserId!.Value, request.VehicleId!.Value, request.StartsAt!.Value, request.EndsAt!.Value);
        var result = await handler.HandleAsync(command, cancellationToken);
        return result.Error is { } error
            ? error.ToProblem()
            : TypedResults.CreatedAtRoute(ReservationResponse.From(result.Value), GetReservationRoute, new { id = result.Value.Id });
    }

    private static async Task<IResult> GetAsync(
        Guid id,
        IQueryHandler<GetReservation.Query, ReservationView> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new GetReservation.Query(id), cancellationToken);
        return result.Error is { } error ? error.ToProblem() : TypedResults.Ok(ReservationResponse.From(result.Value));
    }

    private static async Task<IResult> CancelAsync(
        Guid id,
        ICommandHandler<CancelReservation.Command, ReservationView> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new CancelReservation.Command(id), cancellationToken);
        return result.Error is { } error ? error.ToProblem() : TypedResults.Ok(ReservationResponse.From(result.Value));
    }
}
