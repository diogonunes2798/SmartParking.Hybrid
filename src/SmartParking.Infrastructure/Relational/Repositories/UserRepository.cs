using Microsoft.EntityFrameworkCore;
using SmartParking.Application.Abstractions.Persistence;
using SmartParking.Domain.Users;

namespace SmartParking.Infrastructure.Relational.Repositories;

/// <summary>
/// EF Core implementation of the user aggregate write port (D10, spec 002). Tracked on purpose: the unit of work reads
/// the change tracker for the audit entries of the user, its vehicles and its payment methods. Same in both modes.
/// </summary>
internal sealed class UserRepository(SmartParkingDbContext db) : IUserRepository
{
    private static readonly Func<SmartParkingDbContext, Guid, CancellationToken, Task<User?>> ById =
        EF.CompileAsyncQuery((SmartParkingDbContext context, Guid id, CancellationToken _) =>
            context.Users.FirstOrDefault(user => user.Id == id));

    private static readonly Func<SmartParkingDbContext, Guid, CancellationToken, Task<Vehicle?>> VehicleById =
        EF.CompileAsyncQuery((SmartParkingDbContext context, Guid id, CancellationToken _) =>
            context.Vehicles.FirstOrDefault(vehicle => vehicle.Id == id));

    private static readonly Func<SmartParkingDbContext, Guid, CancellationToken, Task<PaymentMethod?>> PaymentMethodById =
        EF.CompileAsyncQuery((SmartParkingDbContext context, Guid id, CancellationToken _) =>
            context.PaymentMethods.FirstOrDefault(method => method.Id == id));

    public Task<User?> GetByIdAsync(Guid id, CancellationToken cancellationToken) => ById(db, id, cancellationToken);

    public void Add(User user) => db.Users.Add(user);

    public async Task<Vehicle?> GetVehicleAsync(Guid userId, Guid vehicleId, CancellationToken cancellationToken) =>
        await FindVehicleAsync(vehicleId, cancellationToken) is { } vehicle && vehicle.UserId == userId ? vehicle : null;

    public Task<Vehicle?> FindVehicleAsync(Guid vehicleId, CancellationToken cancellationToken) =>
        VehicleById(db, vehicleId, cancellationToken);

    public void AddVehicle(Vehicle vehicle) => db.Vehicles.Add(vehicle);

    public void RemoveVehicle(Vehicle vehicle) => db.Vehicles.Remove(vehicle);

    public async Task<PaymentMethod?> GetPaymentMethodAsync(Guid userId, Guid paymentMethodId, CancellationToken cancellationToken) =>
        await FindPaymentMethodAsync(paymentMethodId, cancellationToken) is { } method && method.UserId == userId ? method : null;

    public Task<PaymentMethod?> FindPaymentMethodAsync(Guid paymentMethodId, CancellationToken cancellationToken) =>
        PaymentMethodById(db, paymentMethodId, cancellationToken);

    public void AddPaymentMethod(PaymentMethod method) => db.PaymentMethods.Add(method);

    public void RemovePaymentMethod(PaymentMethod method) => db.PaymentMethods.Remove(method);
}
