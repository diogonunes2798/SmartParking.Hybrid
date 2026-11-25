using SmartParking.Api.Common;
using SmartParking.Application.Abstractions.Messaging;
using SmartParking.Application.Abstractions.Paging;
using SmartParking.Application.Abstractions.Reading;
using SmartParking.Application.Features.Users;
using SmartParking.Infrastructure.Hosting;

namespace SmartParking.Api.Features.Users;

/// <summary>
/// Drivers, their vehicles and their payment methods (UC-02, spec 002). The provider token of a payment method is
/// accepted on creation and never returned. Anonymous on purpose until F11 (authentication and RBAC).
/// </summary>
internal sealed class UserEndpoints : IEndpointGroup
{
    private const string GetUserRoute = "GetUser";

    public void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/users")
            .WithTags("Users")
            .AllowAnonymous();

        group.MapPost("/", RegisterAsync)
            .WithRequestValidation<RegisterUserRequest>()
            .Produces<UserResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapGet("/{id:guid}", GetAsync)
            .WithName(GetUserRoute)
            .Produces<UserResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{id:guid}/vehicles", RegisterVehicleAsync)
            .WithRequestValidation<RegisterVehicleRequest>()
            .Produces<VehicleResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapGet("/{id:guid}/vehicles", ListVehiclesAsync)
            .WithRequestValidation<PageRequest>()
            .Produces<PagedResponse<VehicleResponse>>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapDelete("/{id:guid}/vehicles/{vehicleId:guid}", RemoveVehicleAsync)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/{id:guid}/payment-methods", AddPaymentMethodAsync)
            .WithRequestValidation<AddPaymentMethodRequest>()
            .Produces<PaymentMethodResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/{id:guid}/payment-methods", ListPaymentMethodsAsync)
            .WithRequestValidation<PageRequest>()
            .Produces<PagedResponse<PaymentMethodResponse>>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapDelete("/{id:guid}/payment-methods/{paymentMethodId:guid}", RemovePaymentMethodAsync)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }

    private static async Task<IResult> RegisterAsync(
        RegisterUserRequest request,
        ICommandHandler<RegisterUser.Command, UserView> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new RegisterUser.Command(request.Name!, request.Email!), cancellationToken);
        return result.Error is { } error
            ? error.ToProblem()
            : TypedResults.CreatedAtRoute(UserResponse.From(result.Value), GetUserRoute, new { id = result.Value.Id });
    }

    private static async Task<IResult> GetAsync(
        Guid id,
        IQueryHandler<GetUser.Query, UserView> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new GetUser.Query(id), cancellationToken);
        return result.Error is { } error ? error.ToProblem() : TypedResults.Ok(UserResponse.From(result.Value));
    }

    private static async Task<IResult> RegisterVehicleAsync(
        Guid id,
        RegisterVehicleRequest request,
        ICommandHandler<RegisterVehicle.Command, VehicleView> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new RegisterVehicle.Command(id, request.LicensePlate!), cancellationToken);
        return result.Error is { } error
            ? error.ToProblem()
            : TypedResults.Created($"/api/users/{id}/vehicles/{result.Value.Id}", VehicleResponse.From(result.Value));
    }

    private static async Task<IResult> ListVehiclesAsync(
        Guid id,
        [AsParameters] PageRequest page,
        IQueryHandler<ListVehicles.Query, PagedResult<VehicleView>> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new ListVehicles.Query(id, page), cancellationToken);
        return result.Error is { } error
            ? error.ToProblem()
            : TypedResults.Ok(new PagedResponse<VehicleResponse>(
                [.. result.Value.Items.Select(VehicleResponse.From)], result.Value.Page, result.Value.PageSize, result.Value.TotalCount));
    }

    private static async Task<IResult> RemoveVehicleAsync(
        Guid id,
        Guid vehicleId,
        ICommandHandler<RemoveVehicle.Command, Guid> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new RemoveVehicle.Command(id, vehicleId), cancellationToken);
        return result.Error is { } error ? error.ToProblem() : TypedResults.NoContent();
    }

    private static async Task<IResult> AddPaymentMethodAsync(
        Guid id,
        AddPaymentMethodRequest request,
        ICommandHandler<AddPaymentMethod.Command, PaymentMethodView> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(request.ToCommand(id), cancellationToken);
        return result.Error is { } error
            ? error.ToProblem()
            : TypedResults.Created($"/api/users/{id}/payment-methods/{result.Value.Id}", PaymentMethodResponse.From(result.Value));
    }

    private static async Task<IResult> ListPaymentMethodsAsync(
        Guid id,
        [AsParameters] PageRequest page,
        IQueryHandler<ListPaymentMethods.Query, PagedResult<PaymentMethodView>> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new ListPaymentMethods.Query(id, page), cancellationToken);
        return result.Error is { } error
            ? error.ToProblem()
            : TypedResults.Ok(new PagedResponse<PaymentMethodResponse>(
                [.. result.Value.Items.Select(PaymentMethodResponse.From)], result.Value.Page, result.Value.PageSize, result.Value.TotalCount));
    }

    private static async Task<IResult> RemovePaymentMethodAsync(
        Guid id,
        Guid paymentMethodId,
        ICommandHandler<RemovePaymentMethod.Command, Guid> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new RemovePaymentMethod.Command(id, paymentMethodId), cancellationToken);
        return result.Error is { } error ? error.ToProblem() : TypedResults.NoContent();
    }
}
