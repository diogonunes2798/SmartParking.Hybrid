using SmartParking.Api.Common;
using SmartParking.Application.Abstractions.Messaging;
using SmartParking.Application.Abstractions.Reading;
using SmartParking.Application.Features.Sessions;
using SmartParking.Infrastructure.Hosting;

namespace SmartParking.Api.Features.Sessions;

/// <summary>
/// Parking sessions: open, read, close and pay (UC-02, RF-02, spec 002). The transitions carry no <c>If-Match</c> and
/// the responses no <c>ETag</c>: each command reads the session, checks its state and saves with the <c>xmin</c> it
/// read; a stale one gives 409. Anonymous on purpose until F11 (authentication and RBAC).
/// </summary>
internal sealed class SessionEndpoints : IEndpointGroup
{
    private const string GetSessionRoute = "GetSession";

    public void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/sessions")
            .WithTags("Sessions")
            .AllowAnonymous();

        group.MapPost("/", OpenAsync)
            .WithRequestValidation<OpenSessionRequest>()
            .Produces<SessionResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapGet("/{id:guid}", GetAsync)
            .WithName(GetSessionRoute)
            .Produces<SessionResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{id:guid}/close", CloseAsync)
            .Produces<SessionResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/{id:guid}/pay", PayAsync)
            .WithRequestValidation<PaySessionRequest>()
            .Produces<PaymentResponse>()
            .ProducesProblem(StatusCodes.Status402PaymentRequired)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }

    private static async Task<IResult> OpenAsync(
        OpenSessionRequest request,
        ICommandHandler<OpenSession.Command, SessionView> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(
            new OpenSession.Command(request.SpotId!.Value, request.VehicleId!.Value, request.ReservationId), cancellationToken);
        return result.Error is { } error
            ? error.ToProblem()
            : TypedResults.CreatedAtRoute(SessionResponse.From(result.Value), GetSessionRoute, new { id = result.Value.Id });
    }

    private static async Task<IResult> GetAsync(
        Guid id,
        IQueryHandler<GetSession.Query, SessionView> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new GetSession.Query(id), cancellationToken);
        return result.Error is { } error ? error.ToProblem() : TypedResults.Ok(SessionResponse.From(result.Value));
    }

    private static async Task<IResult> CloseAsync(
        Guid id,
        ICommandHandler<CloseSession.Command, SessionView> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new CloseSession.Command(id), cancellationToken);
        return result.Error is { } error ? error.ToProblem() : TypedResults.Ok(SessionResponse.From(result.Value));
    }

    private static async Task<IResult> PayAsync(
        Guid id,
        PaySessionRequest request,
        ICommandHandler<PaySession.Command, PaymentReceipt> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new PaySession.Command(id, request.PaymentMethodId!.Value), cancellationToken);
        return result.Error is { } error ? error.ToProblem() : TypedResults.Ok(PaymentResponse.From(result.Value));
    }
}
