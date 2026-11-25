using SmartParking.Application.Abstractions;
using SmartParking.Application.Abstractions.Messaging;
using SmartParking.Application.Abstractions.Persistence;
using SmartParking.Application.Abstractions.Reading;
using SmartParking.Domain.Sessions;

namespace SmartParking.Application.Features.Reservations;

/// <summary>
/// Cancels an active reservation, before or after it starts (spec 002 AC-24). A reservation that is not
/// <c>Active</c> gives 409, so cancelling again is not idempotent; a concurrent change on the same <c>xmin</c> gives
/// 409 too (AC-27). One audit entry and one <c>ReservationCancelled.v1</c> in one transaction.
/// </summary>
public static class CancelReservation
{
    public sealed record Command(Guid Id);

    internal sealed class Handler(IReservationRepository reservations, IUnitOfWork unitOfWork) : ICommandHandler<Command, ReservationView>
    {
        public async Task<Result<ReservationView>> HandleAsync(Command command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            var reservation = await reservations.GetByIdAsync(command.Id, cancellationToken);
            if (reservation is null)
            {
                return ReservationErrors.ReservationNotFound(command.Id);
            }

            if (reservation.Status != ReservationStatus.Active)
            {
                return ReservationErrors.NotActive;
            }

            reservation.Cancel();

            var saved = await unitOfWork.SaveChangesAsync(cancellationToken);
            if (saved.Error is { } error)
            {
                return ReservationErrors.FromSave(error);
            }

            return ReservationView.From(reservation);
        }
    }
}
