using SmartParking.Application.Abstractions;
using SmartParking.Application.Abstractions.Messaging;
using SmartParking.Application.Abstractions.Persistence;
using SmartParking.Application.Abstractions.Reading;
using SmartParking.Domain.Common;
using SmartParking.Domain.Facilities;
using SmartParking.Domain.Sessions;

namespace SmartParking.Application.Features.Reservations;

/// <summary>
/// Reserves a spot for [startsAt, endsAt) (UC-02; spec 002 AC-22, AC-23). Checks, in this order (plan 002b, point 3):
/// 404 spot, user, vehicle; 400 starts in the past, too long, too far ahead, outside one opening interval; 409 spot or
/// facility inactive, vehicle of another user; and on save, 409 when it overlaps another active reservation of the
/// spot (<c>EXCLUDE</c>). One audit entry and one <c>ReservationCreated.v1</c> in one transaction.
/// </summary>
public static class CreateReservation
{
    public sealed record Command(Guid SpotId, Guid UserId, Guid VehicleId, DateTimeOffset StartsAt, DateTimeOffset EndsAt);

    internal sealed class Handler(
        IParkingSpotRepository spots,
        IParkingFacilityRepository facilities,
        IUserRepository users,
        IReservationRepository reservations,
        IUnitOfWork unitOfWork,
        ReservationPolicy policy,
        TimeProvider clock) : ICommandHandler<Command, ReservationView>
    {
        public async Task<Result<ReservationView>> HandleAsync(Command command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            var now = Timestamps.Normalize(clock.GetUtcNow());
            var startsAt = Timestamps.Normalize(command.StartsAt);
            var endsAt = Timestamps.Normalize(command.EndsAt);

            var spot = await spots.GetByIdAsync(command.SpotId, cancellationToken);
            if (spot is null)
            {
                return ReservationErrors.SpotNotFound(command.SpotId);
            }

            if (await users.GetByIdAsync(command.UserId, cancellationToken) is null)
            {
                return ReservationErrors.UserNotFound(command.UserId);
            }

            var vehicle = await users.FindVehicleAsync(command.VehicleId, cancellationToken);
            if (vehicle is null)
            {
                return ReservationErrors.VehicleNotFound(command.VehicleId);
            }

            var facility = await facilities.GetByIdAsync(spot.FacilityId, cancellationToken)
                ?? throw new InvalidOperationException($"Spot {spot.Id} points to a missing facility.");

            if (CheckTimeRules(facility, now, startsAt, endsAt) is { } broken)
            {
                return broken;
            }

            if (spot.Status != SpotStatus.Active)
            {
                return ReservationErrors.SpotInactive;
            }

            if (facility.Status != FacilityStatus.Active)
            {
                return ReservationErrors.FacilityInactive;
            }

            if (vehicle.UserId != command.UserId)
            {
                return ReservationErrors.VehicleOfAnotherUser;
            }

            var reservation = Reservation.Reserve(
                Guid.CreateVersion7(now), spot.Id, command.UserId, command.VehicleId, startsAt, endsAt);
            reservations.Add(reservation);

            var saved = await unitOfWork.SaveChangesAsync(cancellationToken);
            if (saved.Error is { } error)
            {
                return ReservationErrors.FromCreate(error);
            }

            return ReservationView.From(reservation);
        }

        private Error? CheckTimeRules(ParkingFacility facility, DateTimeOffset now, DateTimeOffset startsAt, DateTimeOffset endsAt)
        {
            if (startsAt < now)
            {
                return ReservationErrors.StartsInPast;
            }

            if (endsAt - startsAt > policy.MaxDuration)
            {
                return ReservationErrors.TooLong(policy.MaxDuration);
            }

            if (startsAt > now + policy.MaxAdvance)
            {
                return ReservationErrors.TooFarAhead(policy.MaxAdvance);
            }

            return facility.FitsInOneOpeningInterval(startsAt, endsAt) ? null : ReservationErrors.OutsideOpeningHours;
        }
    }
}
