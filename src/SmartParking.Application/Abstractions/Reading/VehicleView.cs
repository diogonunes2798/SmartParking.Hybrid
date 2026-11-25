using SmartParking.Domain.Users;

namespace SmartParking.Application.Abstractions.Reading;

/// <summary>Read model of a vehicle, with the normalised licence plate.</summary>
public sealed record VehicleView(Guid Id, Guid UserId, string LicensePlate)
{
    public static VehicleView From(Vehicle vehicle)
    {
        ArgumentNullException.ThrowIfNull(vehicle);
        return new VehicleView(vehicle.Id, vehicle.UserId, vehicle.LicensePlate);
    }
}
