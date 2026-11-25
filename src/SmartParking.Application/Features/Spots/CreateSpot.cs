using SmartParking.Application.Abstractions;
using SmartParking.Application.Abstractions.Messaging;
using SmartParking.Application.Abstractions.Persistence;
using SmartParking.Application.Abstractions.Reading;
using SmartParking.Application.Features.Facilities;
using SmartParking.Domain.Facilities;

namespace SmartParking.Application.Features.Spots;

/// <summary>
/// Adds one spot to a facility (RF-06): 404 for an unknown facility; 400 for a zone of another facility; 409 for a
/// code already used in the facility (unique index, AC-11). New spots are <c>Active</c>.
/// </summary>
public static class CreateSpot
{
    public sealed record Command(Guid FacilityId, Guid ZoneId, string Code, SpotType Type);

    internal sealed class Handler(
        IParkingFacilityRepository facilities,
        IZoneRepository zones,
        IParkingSpotRepository spots,
        IUnitOfWork unitOfWork,
        TimeProvider clock) : ICommandHandler<Command, SpotView>
    {
        public async Task<Result<SpotView>> HandleAsync(Command command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            if (!await facilities.ExistsAsync(command.FacilityId, cancellationToken))
            {
                return FacilityErrors.NotFound(command.FacilityId);
            }

            var zone = await zones.GetByIdAsync(command.ZoneId, cancellationToken);
            if (zone is null || zone.FacilityId != command.FacilityId)
            {
                return SpotErrors.ZoneNotInFacility("zoneId");
            }

            var spot = ParkingSpot.Create(
                Guid.CreateVersion7(clock.GetUtcNow()), command.FacilityId, command.ZoneId, command.Code, command.Type);
            spots.Add(spot);

            var saved = await unitOfWork.SaveChangesAsync(cancellationToken);
            return saved.Error is { } error ? error : SpotView.From(spot);
        }
    }
}
