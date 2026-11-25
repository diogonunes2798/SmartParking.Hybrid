using SmartParking.Application.Abstractions;
using SmartParking.Application.Abstractions.Messaging;
using SmartParking.Application.Abstractions.Persistence;
using SmartParking.Application.Abstractions.Reading;
using SmartParking.Domain.Sessions;

namespace SmartParking.Application.Features.Sessions;

/// <summary>
/// Closes an open session now and prices it with the session's own tariff version (spec 002 AC-9, AC-10). It can
/// close outside the opening hours. A session that is not <c>Open</c> gives 409; a concurrent change on the same
/// <c>xmin</c> gives 409 too (AC-11). One audit entry and one <c>SessionClosed.v1</c> in one transaction.
/// </summary>
public static class CloseSession
{
    public sealed record Command(Guid Id);

    internal sealed class Handler(
        IParkingSessionRepository sessions,
        ITariffRepository tariffs,
        IUnitOfWork unitOfWork,
        TimeProvider clock) : ICommandHandler<Command, SessionView>
    {
        public async Task<Result<SessionView>> HandleAsync(Command command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            var session = await sessions.GetByIdAsync(command.Id, cancellationToken);
            if (session is null)
            {
                return SessionErrors.SessionNotFound(command.Id);
            }

            if (session.Status != SessionStatus.Open)
            {
                return SessionErrors.NotOpen;
            }

            var tariff = await tariffs.GetByIdAsync(session.TariffId, cancellationToken)
                ?? throw new InvalidOperationException($"Session {session.Id} points to a missing tariff version.");

            session.Close(tariff, clock.GetUtcNow());

            var saved = await unitOfWork.SaveChangesAsync(cancellationToken);
            if (saved.Error is { } error)
            {
                return SessionErrors.FromSave(error);
            }

            return SessionView.From(session, tariff.Currency);
        }
    }
}
