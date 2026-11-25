using SmartParking.Application.Abstractions;
using SmartParking.Application.Abstractions.Messaging;
using SmartParking.Application.Abstractions.Persistence;

namespace SmartParking.Application.Features.Users;

/// <summary>
/// Removes a vehicle of a user. While a session or reservation refers to it, the database refuses it (FK RESTRICT)
/// and the whole transaction, audit and outbox included, is rolled back (spec 002 AC-3, 409).
/// </summary>
public static class RemoveVehicle
{
    public sealed record Command(Guid UserId, Guid VehicleId);

    internal sealed class Handler(IUserRepository users, IUnitOfWork unitOfWork) : ICommandHandler<Command, Guid>
    {
        public async Task<Result<Guid>> HandleAsync(Command command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            var user = await users.GetByIdAsync(command.UserId, cancellationToken);
            if (user is null)
            {
                return UserErrors.NotFound(command.UserId);
            }

            var vehicle = await users.GetVehicleAsync(command.UserId, command.VehicleId, cancellationToken);
            if (vehicle is null)
            {
                return UserErrors.VehicleNotFound(command.VehicleId);
            }

            user.RemoveVehicle(vehicle);
            users.RemoveVehicle(vehicle);

            var saved = await unitOfWork.SaveChangesAsync(cancellationToken);
            if (saved.Error is { } error)
            {
                return error;
            }

            return vehicle.Id;
        }
    }
}
