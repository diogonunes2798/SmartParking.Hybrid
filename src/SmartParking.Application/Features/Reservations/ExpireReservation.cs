using SmartParking.Application.Abstractions;
using SmartParking.Application.Abstractions.Messaging;
using SmartParking.Application.Abstractions.Persistence;
using SmartParking.Domain.Common;
using SmartParking.Domain.Sessions;

namespace SmartParking.Application.Features.Reservations;

/// <summary>
/// Expires one reservation in its own transaction (spec 002 AC-28; expiry worker). It reads the reservation again,
/// tracked: one that is no longer <c>Active</c>, or not yet due, is skipped without writing. A stale <c>xmin</c> on
/// save (another request fulfilled or cancelled it meanwhile) gives 409, which the worker counts and ignores: the
/// reservation stays as the other request left it. One audit entry (actor <c>system</c>) and one
/// <c>ReservationExpired.v1</c>.
/// </summary>
public static class ExpireReservation
{
    public sealed record Command(Guid ReservationId);

    internal sealed class Handler(
        IReservationRepository reservations,
        IUnitOfWork unitOfWork,
        ReservationPolicy policy,
        TimeProvider clock) : ICommandHandler<Command, ExpiryOutcome>
    {
        public async Task<Result<ExpiryOutcome>> HandleAsync(Command command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            var now = Timestamps.Normalize(clock.GetUtcNow());
            var reservation = await reservations.GetByIdAsync(command.ReservationId, cancellationToken);
            if (reservation is null
                || reservation.Status != ReservationStatus.Active
                || !policy.IsDue(reservation.StartsAt, now))
            {
                return ExpiryOutcome.Skipped;
            }

            reservation.Expire(now, policy);

            var saved = await unitOfWork.SaveChangesAsync(cancellationToken);
            if (saved.Error is { } error)
            {
                return ReservationErrors.FromSave(error);
            }

            return ExpiryOutcome.Expired;
        }
    }
}
