using SmartParking.Application.Abstractions;
using SmartParking.Application.Abstractions.Messaging;
using SmartParking.Application.Abstractions.Persistence;
using SmartParking.Application.Abstractions.Reading;
using SmartParking.Domain.Common;
using SmartParking.Domain.Facilities;
using SmartParking.Domain.Sessions;

namespace SmartParking.Application.Features.Sessions;

/// <summary>
/// Opens a session on a spot (UC-02; spec 002 AC-5 to AC-7). The session keeps the tariff version in force now until
/// it closes (ADR 0005). Preconditions, each 409 without writing anything: the spot and the facility are active, the
/// facility is open now in its time zone, a tariff is in force, and the vehicle has no other open session (two
/// concurrent opens are caught by the partial unique index, also 409). One audit entry and one
/// <c>SessionOpened.v1</c> in one transaction.
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

            if (command.ReservationId is not null)
            {
                return SessionErrors.ReservationNotSupported;
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

            var session = ParkingSession.Open(Guid.CreateVersion7(now), spot.Id, command.VehicleId, tariff.Id, null, now, policy);
            sessions.Add(session);

            var saved = await unitOfWork.SaveChangesAsync(cancellationToken);
            if (saved.Error is { } error)
            {
                return SessionErrors.FromSave(error);
            }

            return SessionView.From(session, tariff.Currency);
        }
    }
}
