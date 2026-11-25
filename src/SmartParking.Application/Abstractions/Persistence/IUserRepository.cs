using SmartParking.Domain.Users;

namespace SmartParking.Application.Abstractions.Persistence;

/// <summary>
/// Write port of the user aggregate: the user, its vehicles and its payment methods (D10, ADR 0003).
/// The children are loaded and added through the root's repository. No <c>IQueryable</c>, no save.
/// </summary>
public interface IUserRepository
{
    Task<User?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    void Add(User user);

    /// <summary>The vehicle of this user, or <c>null</c> when it does not exist or belongs to someone else.</summary>
    Task<Vehicle?> GetVehicleAsync(Guid userId, Guid vehicleId, CancellationToken cancellationToken);

    /// <summary>A vehicle by id, whoever owns it.</summary>
    Task<Vehicle?> FindVehicleAsync(Guid vehicleId, CancellationToken cancellationToken);

    void AddVehicle(Vehicle vehicle);

    void RemoveVehicle(Vehicle vehicle);

    /// <summary>The payment method of this user, or <c>null</c> when it does not exist or belongs to someone else.</summary>
    Task<PaymentMethod?> GetPaymentMethodAsync(Guid userId, Guid paymentMethodId, CancellationToken cancellationToken);

    /// <summary>A payment method by id, whoever owns it.</summary>
    Task<PaymentMethod?> FindPaymentMethodAsync(Guid paymentMethodId, CancellationToken cancellationToken);

    void AddPaymentMethod(PaymentMethod method);

    void RemovePaymentMethod(PaymentMethod method);
}
