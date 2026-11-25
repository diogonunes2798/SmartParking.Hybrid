using SmartParking.Application.Abstractions;
using SmartParking.Application.Abstractions.Messaging;
using SmartParking.Application.Abstractions.Reading;

namespace SmartParking.Application.Features.Users;

/// <summary>Reads one user; 404 when it does not exist.</summary>
public static class GetUser
{
    public sealed record Query(Guid Id);

    internal sealed class Handler(IUserReader reader) : IQueryHandler<Query, UserView>
    {
        public async Task<Result<UserView>> HandleAsync(Query query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);

            var user = await reader.GetAsync(query.Id, cancellationToken);
            if (user is null)
            {
                return UserErrors.NotFound(query.Id);
            }

            return user;
        }
    }
}
