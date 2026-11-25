using SmartParking.Application.Abstractions;
using SmartParking.Application.Abstractions.Messaging;
using SmartParking.Application.Abstractions.Persistence;
using SmartParking.Application.Abstractions.Reading;
using SmartParking.Domain.Common;
using SmartParking.Domain.Facilities;
using SmartParking.Domain.Sessions;

namespace SmartParking.Application.Features.Sessions;

/// <summary>
/// Opens a session on a spot (UC-02; spec 002 AC-5 to AC-7, AC-25, AC-26). The session keeps the tariff version in
/// force now until it closes (ADR 0005). Order of the checks (plan 002b):
/// <list type="number">
/// <item>404: spot, vehicle, and the reservation when the request names one;</item>
/// <item>with a reservation, 409 when it is for another spot or vehicle, not <c>Active</c>, or now is outside its
/// fulfilment window;</item>
/// <item>409: the spot and the facility are active, the facility is open now in its time zone, a tariff is in force,
/// and the vehicle has no other open session (two concurrent opens are caught by the partial unique index, also 409);</item>
/// <item>without a reservation, 409 when the spot is held by an active reservation of another vehicle (spot
/// protection).</item>
/// </list>
/// One transaction: the session, its audit entry and one <c>SessionOpened.v1</c>; with a reservation, the reservation
/// becomes <c>Fulfilled</c> in the same transaction and gets its own audit entry (ADR 0006, ADR 0007).
/// </summary>
public static class OpenSession
{
    public sealed record Command(Guid SpotId, Guid VehicleId, Guid? ReservationId);

    internal sealed class Handler(
        IParkingSpotRepository spots,
        IParkingFacilityRepository facilities,
        IUserRepository users,
        ITariffRepository tariffs,
        IParkingSessionRepository sessions,
        IReservationRepository reservations,
        IUnitOfWork unitOfWork,
        ReservationPolicy policy,
        TimeProvider clock) : ICommandHandler<Command, SessionView>
    {
        public async Task<Result<SessionView>> HandleAsync(Command command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            var now = Timestamps.Normalize(clock.GetUtcNow());

            var spot = await spots.GetByIdAsync(command.SpotId, cancellationToken);
            if (spot is null)
            {
                return SessionErrors.SpotNotFound(command.SpotId);
            }

            if (await users.FindVehicleAsync(command.VehicleId, cancellationToken) is null)
            {
                return SessionErrors.VehicleNotFound(command.VehicleId);
            }

            Reservation? reservation = null;
            if (command.ReservationId is { } reservationId)
            {
                reservation = await reservations.GetByIdAsync(reservationId, cancellationToken);
                if (reservation is null)
                {
                    return SessionErrors.ReservationNotFound(reservationId);
                }

                if (CheckFulfilment(reservation, command, now) is { } notFulfilled)
                {
                    return notFulfilled;
                }
            }

            var facility = await facilities.GetByIdAsync(spot.FacilityId, cancellationToken)
                ?? throw new InvalidOperationException($"Spot {spot.Id} points to a missing facility.");

            if (spot.Status != SpotStatus.Active)
            {
                return SessionErrors.SpotInactive;
            }

            if (facility.Status != FacilityStatus.Active)
            {
                return SessionErrors.FacilityInactive;
            }

            if (!facility.IsOpenAt(now))
            {
                return SessionErrors.FacilityClosed;
            }

            var tariff = await tariffs.GetInForceAsync(facility.Id, now, cancellationToken);
            if (tariff is null)
            {
                return SessionErrors.NoTariffInForce;
            }

            if (await sessions.GetOpenByVehicleAsync(command.VehicleId, cancellationToken) is not null)
            {
                return SessionErrors.VehicleHasOpenSession;
            }

            if (reservation is null
                && await reservations.IsSpotHeldForAnotherVehicleAsync(spot.Id, command.VehicleId, now, policy, cancellationToken))
            {
                return SessionErrors.SpotReserved;
            }

            var session = ParkingSession.Open(Guid.CreateVersion7(now), spot.Id, command.VehicleId, tariff.Id, reservation, now, policy);
            sessions.Add(session);

            // A fulfilled reservation is tracked and Modified: the save checks its xmin too (AC-27; ADR 0007).
            var saved = await unitOfWork.SaveChangesAsync(cancellationToken);
            if (saved.Error is { } error)
            {
                return SessionErrors.FromSave(error);
            }

            return SessionView.From(session, tariff.Currency);
        }

        /// <summary>The three fulfilment conditions (spec 002), each 409: same spot and vehicle, Active, inside the window.</summary>
        private Error? CheckFulfilment(Reservation reservation, Command command, DateTimeOffset now)
        {
            if (reservation.SpotId != command.SpotId || reservation.VehicleId != command.VehicleId)
            {
                return SessionErrors.ReservationMismatch;
            }

            if (reservation.Status != ReservationStatus.Active)
            {
                return SessionErrors.ReservationNotActive;
            }

            return policy.FulfilmentWindow(reservation.StartsAt).Contains(now) ? null : SessionErrors.OutsideFulfilmentWindow;
        }
    }
}
