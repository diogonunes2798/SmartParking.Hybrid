using SmartParking.Application.Abstractions;
using SmartParking.Application.Common;
using SmartParking.Domain.Facilities;
using SmartParking.Domain.Users;

namespace SmartParking.Application.Users;

// Minimal creation of users and vehicles: a precondition of UC-02 (a session needs a driver and a vehicle).
// Not part of RF-06 (configuration CRUD); only what the Swagger test and the load scenarios need. No events.

public sealed record CreateUser(string DisplayName, string Email);

public sealed record CreateVehicle(string LicensePlate, VehicleType Type);

public sealed record UserDto(Guid Id, string DisplayName, string Email, uint Version)
{
    public static UserDto From(User u)
    {
        ArgumentNullException.ThrowIfNull(u);
        return new(u.Id, u.DisplayName, u.Email, u.Version);
    }
}

public sealed record VehicleDto(Guid Id, Guid UserId, string LicensePlate, VehicleType Type, uint Version)
{
    public static VehicleDto From(Vehicle v)
    {
        ArgumentNullException.ThrowIfNull(v);
        return new(v.Id, v.UserId, v.LicensePlate, v.Type, v.Version);
    }
}

public sealed class UserCommands(IRepository<User> users, IRepository<Vehicle> vehicles, IUnitOfWork unitOfWork)
{
    public async Task<UserDto> CreateUserAsync(CreateUser command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var user = User.Create(command.DisplayName, command.Email);
        users.Add(user);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return UserDto.From(user);
    }

    public async Task<VehicleDto> CreateVehicleAsync(Guid userId, CreateVehicle command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!await users.ExistsAsync(userId, cancellationToken).ConfigureAwait(false))
        {
            throw new NotFoundException("User", userId);
        }

        var vehicle = Vehicle.Create(userId, command.LicensePlate, command.Type);
        vehicles.Add(vehicle);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return VehicleDto.From(vehicle);
    }
}
