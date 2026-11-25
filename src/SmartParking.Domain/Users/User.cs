using SmartParking.Domain.Common;
using SmartParking.Domain.Users.Events;

namespace SmartParking.Domain.Users;

/// <summary>
/// Driver account and root of the user aggregate (vehicles and payment methods). Name and email are personal data:
/// never log them at Information level or below. The vehicles and payment methods are not loaded with the user;
/// their changes are raised here, with the child as the only changed entity (spec 002, ADR 0006), so the user row is
/// not touched.
/// </summary>
public sealed class User : AggregateRoot
{
    public const int NameMaxLength = 200;

    public const int EmailMaxLength = 254;

    private User()
    {
    }

    public string Name { get; private set; } = string.Empty;

    /// <summary>Stored in lower case; unique.</summary>
    public string Email { get; private set; } = string.Empty;

    public static User Register(Guid id, string name, string email)
    {
        var user = new User
        {
            Id = Guard.Id(id),
            Name = Guard.Text(name, NameMaxLength),
            Email = NormalizeEmail(email),
        };

        user.Raise(new UserRegistered(user.Id));
        return user;
    }

    public static string NormalizeEmail(string email) =>
        Guard.Text(email, EmailMaxLength).ToLowerInvariant();

    public Vehicle RegisterVehicle(Guid vehicleId, string licensePlate)
    {
        var vehicle = Vehicle.Create(vehicleId, Id, licensePlate);
        Raise(new VehicleRegistered(vehicle.Id, Id, vehicle.LicensePlate), vehicle);
        return vehicle;
    }

    /// <summary>Records the removal. The database refuses it (FK RESTRICT) while a session or reservation refers to it.</summary>
    public void RemoveVehicle(Vehicle vehicle)
    {
        ArgumentNullException.ThrowIfNull(vehicle);
        EnsureOwns(vehicle.UserId, nameof(vehicle));

        Raise(new VehicleRemoved(vehicle.Id, Id, vehicle.LicensePlate), vehicle);
    }

    public PaymentMethod AddPaymentMethod(
        Guid paymentMethodId,
        PaymentMethodType type,
        string providerToken,
        string maskedNumber,
        DateOnly? expiresOn)
    {
        var method = PaymentMethod.Create(paymentMethodId, Id, type, providerToken, maskedNumber, expiresOn);
        Raise(new PaymentMethodAdded(method.Id, Id, method.Type, method.MaskedNumber, method.ExpiresOn), method);
        return method;
    }

    /// <summary>Records the removal. The database refuses it (FK RESTRICT) while a payment refers to it.</summary>
    public void RemovePaymentMethod(PaymentMethod method)
    {
        ArgumentNullException.ThrowIfNull(method);
        EnsureOwns(method.UserId, nameof(method));

        Raise(new PaymentMethodRemoved(method.Id, Id, method.Type, method.MaskedNumber, method.ExpiresOn), method);
    }

    private void EnsureOwns(Guid ownerId, string paramName)
    {
        if (ownerId != Id)
        {
            throw new ArgumentException("It belongs to another user.", paramName);
        }
    }
}
