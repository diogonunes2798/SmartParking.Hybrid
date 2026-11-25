using SmartParking.Application.Abstractions;
using SmartParking.Application.Abstractions.Messaging;
using SmartParking.Application.Abstractions.Reading;
using SmartParking.Domain.Common;
using SmartParking.Domain.Sessions;

namespace SmartParking.Application.Features.Reservations;

/// <summary>
/// The <c>Active</c> reservations past their grace period now, oldest first (spec 002, «Integrations»). Only
/// identifiers: the expiry worker expires each one in its own transaction with <see cref="ExpireReservation"/>.
/// </summary>
public static class ListDueReservations
{
    public sealed record Query;

    internal sealed class Handler(IReservationReader reader, ReservationPolicy policy, TimeProvider clock)
        : IQueryHandler<Query, IReadOnlyList<Guid>>
    {
        public async Task<Result<IReadOnlyList<Guid>>> HandleAsync(Query query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);

            // Due means now > startsAt + grace, that is startsAt < now − grace (ReservationPolicy.IsDue).
            var now = Timestamps.Normalize(clock.GetUtcNow());
            var due = await reader.ListDueIdsAsync(now - policy.ExpiryGrace, cancellationToken);
            return Result.Success(due);
        }
    }
}
