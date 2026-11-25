using Microsoft.EntityFrameworkCore;
using SmartParking.Application.Abstractions.Paging;
using SmartParking.Application.Abstractions.Reading;

namespace SmartParking.Infrastructure.Relational.Reading;

/// <summary>
/// User read port over PostgreSQL: <c>AsNoTracking</c> and projections to the views. The payment method projection
/// never selects the provider token (spec 002, AC-4).
/// </summary>
internal sealed class UserReader(SmartParkingDbContext db) : IUserReader
{
    public Task<UserView?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        db.Users.AsNoTracking()
            .Where(user => user.Id == id)
            .Select(user => new UserView(user.Id, user.Name, user.Email))
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<PagedResult<VehicleView>?> ListVehiclesAsync(Guid userId, PageRequest page, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);

        if (!await UserExistsAsync(userId, cancellationToken))
        {
            return null;
        }

        var vehicles = db.Vehicles.AsNoTracking().Where(vehicle => vehicle.UserId == userId);
        var totalCount = await vehicles.CountAsync(cancellationToken);
        var items = await vehicles.OrderBy(vehicle => vehicle.Id).Skip(page.Offset).Take(page.PageSize)
            .Select(vehicle => new VehicleView(vehicle.Id, vehicle.UserId, vehicle.LicensePlate))
            .ToListAsync(cancellationToken);

        return new PagedResult<VehicleView>(items, page.Page, page.PageSize, totalCount);
    }

    public async Task<PagedResult<PaymentMethodView>?> ListPaymentMethodsAsync(Guid userId, PageRequest page, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);

        if (!await UserExistsAsync(userId, cancellationToken))
        {
            return null;
        }

        var methods = db.PaymentMethods.AsNoTracking().Where(method => method.UserId == userId);
        var totalCount = await methods.CountAsync(cancellationToken);
        var items = await methods.OrderBy(method => method.Id).Skip(page.Offset).Take(page.PageSize)
            .Select(method => new PaymentMethodView(method.Id, method.UserId, method.Type, method.MaskedNumber, method.ExpiresOn))
            .ToListAsync(cancellationToken);

        return new PagedResult<PaymentMethodView>(items, page.Page, page.PageSize, totalCount);
    }

    private Task<bool> UserExistsAsync(Guid userId, CancellationToken cancellationToken) =>
        db.Users.AnyAsync(user => user.Id == userId, cancellationToken);
}
