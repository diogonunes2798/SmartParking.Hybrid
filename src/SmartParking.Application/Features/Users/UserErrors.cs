using SmartParking.Application.Abstractions;

namespace SmartParking.Application.Features.Users;

internal static class UserErrors
{
    public static Error NotFound(Guid id) =>
        Error.NotFound("User.NotFound", $"User {id} does not exist.");

    public static Error VehicleNotFound(Guid id) =>
        Error.NotFound("Vehicle.NotFound", $"Vehicle {id} does not exist for this user.");

    public static Error PaymentMethodNotFound(Guid id) =>
        Error.NotFound("PaymentMethod.NotFound", $"Payment method {id} does not exist for this user.");
}
