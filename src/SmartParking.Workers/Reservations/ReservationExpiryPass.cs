using SmartParking.Application.Abstractions;
using SmartParking.Application.Abstractions.Messaging;
using SmartParking.Application.Features.Reservations;

namespace SmartParking.Workers.Reservations;

/// <summary>
/// One expiry pass (spec 002, «Integrations»; plan 002b, proposal 10): lists the due reservations in one scope and
/// expires each one in its own scope, so each has its own <c>DbContext</c> and its own transaction. A stale
/// <c>xmin</c> (409) on one reservation is counted and does not stop the others (AC-28). Logs identifiers and counts
/// only, never personal data.
/// </summary>
internal sealed partial class ReservationExpiryPass(IServiceScopeFactory scopes, ILogger<ReservationExpiryPass> logger)
{
    public async Task<ExpiryPassResult> RunAsync(CancellationToken cancellationToken)
    {
        var due = await ListDueAsync(cancellationToken);
        int expired = 0, conflicts = 0, skipped = 0;

        foreach (var reservationId in due)
        {
            await using var scope = scopes.CreateAsyncScope();
            var handler = scope.ServiceProvider.GetRequiredService<ICommandHandler<ExpireReservation.Command, ExpiryOutcome>>();
            var result = await handler.HandleAsync(new ExpireReservation.Command(reservationId), cancellationToken);

            if (result.Error is { } error)
            {
                if (error.Type != ErrorType.Conflict)
                {
                    throw new InvalidOperationException($"Expiring reservation {reservationId} failed with {error.Code}.");
                }

                conflicts++;
                LogConflict(logger, reservationId, error.Code);
                continue;
            }

            if (result.Value == ExpiryOutcome.Expired)
            {
                expired++;
            }
            else
            {
                skipped++;
            }
        }

        var summary = new ExpiryPassResult(expired, conflicts, skipped);
        if (due.Count > 0)
        {
            LogPass(logger, due.Count, expired, conflicts, skipped);
        }

        return summary;
    }

    private async Task<IReadOnlyList<Guid>> ListDueAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<IQueryHandler<ListDueReservations.Query, IReadOnlyList<Guid>>>();
        var result = await handler.HandleAsync(new ListDueReservations.Query(), cancellationToken);
        return result.Value;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information,
        Message = "Reservation expiry pass: {Due} due, {Expired} expired, {Conflicts} conflicts, {Skipped} skipped")]
    private static partial void LogPass(ILogger logger, int due, int expired, int conflicts, int skipped);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information,
        Message = "Reservation {ReservationId} was changed by another request before it expired ({ErrorCode}); left as it is")]
    private static partial void LogConflict(ILogger logger, Guid reservationId, string errorCode);
}
