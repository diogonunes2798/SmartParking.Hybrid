using SmartParking.Application.Abstractions;
using SmartParking.Application.Abstractions.Messaging;
using SmartParking.Application.Abstractions.Paging;
using SmartParking.Application.Abstractions.Reading;

namespace SmartParking.Application.Features.Users;

/// <summary>Lists the payment methods of a user page by page, never with the provider token; 404 when the user does not exist.</summary>
public static class ListPaymentMethods
{
    public sealed record Query(Guid UserId, PageRequest Page);

    internal sealed class Handler(IUserReader reader) : IQueryHandler<Query, PagedResult<PaymentMethodView>>
    {
        public async Task<Result<PagedResult<PaymentMethodView>>> HandleAsync(Query query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);

            var page = await reader.ListPaymentMethodsAsync(query.UserId, query.Page, cancellationToken);
            if (page is null)
            {
                return UserErrors.NotFound(query.UserId);
            }

            return page;
        }
    }
}
