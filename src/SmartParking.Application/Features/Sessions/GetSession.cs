using SmartParking.Application.Abstractions;
using SmartParking.Application.Abstractions.Messaging;
using SmartParking.Application.Abstractions.Reading;

namespace SmartParking.Application.Features.Sessions;

/// <summary>Reads one session (spec 002 AC-19): no amount while open; 404 when it does not exist.</summary>
public static class GetSession
{
    public sealed record Query(Guid Id);

    internal sealed class Handler(ISessionReader reader) : IQueryHandler<Query, SessionView>
    {
        public async Task<Result<SessionView>> HandleAsync(Query query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);

            var session = await reader.GetAsync(query.Id, cancellationToken);
            if (session is null)
            {
                return SessionErrors.SessionNotFound(query.Id);
            }

            return session;
        }
    }
}
