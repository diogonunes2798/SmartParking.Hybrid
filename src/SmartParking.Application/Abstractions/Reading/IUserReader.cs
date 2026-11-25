using SmartParking.Application.Abstractions.Paging;

namespace SmartParking.Application.Abstractions.Reading;

/// <summary>
/// Read port of the user queries (D10: no repository on the read side). Projections and <c>AsNoTracking</c>.
/// The lists return <c>null</c> when the user does not exist (404). Never exposes the provider token.
/// </summary>
public interface IUserReader
{
    Task<UserView?> GetAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>One page of the user's vehicles in creation order (UUID v7), or <c>null</c> when the user does not exist.</summary>
    Task<PagedResult<VehicleView>?> ListVehiclesAsync(Guid userId, PageRequest page, CancellationToken cancellationToken);

    /// <summary>One page of the user's payment methods in creation order, or <c>null</c> when the user does not exist.</summary>
    Task<PagedResult<PaymentMethodView>?> ListPaymentMethodsAsync(Guid userId, PageRequest page, CancellationToken cancellationToken);
}
