using SmartParking.Application.Abstractions;
using SmartParking.Application.Abstractions.Messaging;
using SmartParking.Application.Abstractions.Persistence;
using SmartParking.Application.Abstractions.Reading;
using SmartParking.Domain.Facilities;

namespace SmartParking.Application.Features.Spots;

/// <summary>
/// Replaces a spot's zone, code, type and status (PUT). Needs the version the client read (428/412). The zone can
/// change only within the facility: another facility's zone is 400 (AC-16). <c>SpotUpdated.v1</c> carries the previous zone.
/// A request that changes nothing writes nothing and keeps the ETag.
/// </summary>
public static class UpdateSpot
{
    public sealed record Command(
        Guid FacilityId, Guid SpotId, uint? ExpectedVersion, Guid ZoneId, string Code, SpotType Type, SpotStatus Status);

    internal sealed class Handler(IParkingSpotRepository spots, IZoneRepository zones, IUnitOfWork unitOfWork)
        : ICommandHandler<Command, SpotView>
    {
        public async Task<Result<SpotView>> HandleAsync(Command command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            if (command.ExpectedVersion is null)
            {
                return ConcurrencyCheck.VersionRequired;
            }

            var spot = await spots.GetByIdAsync(command.SpotId, cancellationToken);
            if (spot is null || spot.FacilityId != command.FacilityId)
            {
                return SpotErrors.NotFound(command.FacilityId, command.SpotId);
            }

            if (ConcurrencyCheck.Verify(command.ExpectedVersion, spot.Version) is { } stale)
            {
                return stale;
            }

            if (command.ZoneId != spot.ZoneId)
            {
                var zone = await zones.GetByIdAsync(command.ZoneId, cancellationToken);
                if (zone is null || zone.FacilityId != spot.FacilityId)
                {
                    return SpotErrors.ZoneNotInFacility("zoneId");
                }
            }

            var changed = spot.Update(command.ZoneId, command.Code, command.Type, command.Status);
            if (changed && (await unitOfWork.SaveChangesAsync(cancellationToken)).Error is { } error)
            {
                return error;
            }

            return SpotView.From(spot);
        }
    }
}
