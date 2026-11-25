using SmartParking.Application.Abstractions;
using SmartParking.Application.Abstractions.Messaging;
using SmartParking.Application.Abstractions.Persistence;
using SmartParking.Application.Abstractions.Reading;

namespace SmartParking.Application.Features.Users;

/// <summary>
/// Registers a vehicle of a user (spec 002 AC-3). The licence plate is normalised (<c>aa-12 bb</c> → <c>AA12BB</c>);
/// a repeated plate is refused by the unique index (409). The user raises <c>VehicleRegistered.v1</c> with the
/// vehicle as the only changed entity: one audit entry, for the vehicle (ADR 0006).
/// </summary>
public static class RegisterVehicle
{
    public sealed record Command(Guid UserId, string LicensePlate);

    internal sealed class Handler(IUserRepository users, IUnitOfWork unitOfWork, TimeProvider clock)
        : ICommandHandler<Command, VehicleView>
    {
        public async Task<Result<VehicleView>> HandleAsync(Command command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            var user = await users.GetByIdAsync(command.UserId, cancellationToken);
            if (user is null)
            {
                return UserErrors.NotFound(command.UserId);
            }

            var vehicle = user.RegisterVehicle(Guid.CreateVersion7(clock.GetUtcNow()), command.LicensePlate);
            users.AddVehicle(vehicle);

            var saved = await unitOfWork.SaveChangesAsync(cancellationToken);
            if (saved.Error is { } error)
            {
                return error;
            }

            return VehicleView.From(vehicle);
        }
    }
}
