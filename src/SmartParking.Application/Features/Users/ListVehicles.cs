using SmartParking.Application.Abstractions;
using SmartParking.Application.Abstractions.Messaging;
using SmartParking.Application.Abstractions.Paging;
using SmartParking.Application.Abstractions.Reading;

namespace SmartParking.Application.Features.Users;

/// <summary>Lists the vehicles of a user page by page; 404 when the user does not exist.</summary>
public static class ListVehicles
{
    public sealed record Query(Guid UserId, PageRequest Page);

    internal sealed class Handler(IUserReader reader) : IQueryHandler<Query, PagedResult<VehicleView>>
    {
        public async Task<Result<PagedResult<VehicleView>>> HandleAsync(Query query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);

            var page = await reader.ListVehiclesAsync(query.UserId, query.Page, cancellationToken);
            if (page is null)
            {
                return UserErrors.NotFound(query.UserId);
            }

            return page;
        }
    }
}
