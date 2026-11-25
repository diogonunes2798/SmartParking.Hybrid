using SmartParking.Application.Abstractions;
using SmartParking.Application.Abstractions.Messaging;
using SmartParking.Application.Abstractions.Persistence;
using SmartParking.Application.Abstractions.Reading;

namespace SmartParking.Application.Features.Zones;

/// <summary>
/// Renames a zone (PUT). Needs the version the client read (428 without it, 412 when stale). A zone of another
/// facility is 404. A request that changes nothing writes nothing and keeps the ETag.
/// </summary>
public static class UpdateZone
{
    public sealed record Command(Guid FacilityId, Guid ZoneId, uint? ExpectedVersion, string Name);

    internal sealed class Handler(IZoneRepository zones, IUnitOfWork unitOfWork) : ICommandHandler<Command, ZoneView>
    {
        public async Task<Result<ZoneView>> HandleAsync(Command command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            if (command.ExpectedVersion is null)
            {
                return ConcurrencyCheck.VersionRequired;
            }

            var zone = await zones.GetByIdAsync(command.ZoneId, cancellationToken);
            if (zone is null || zone.FacilityId != command.FacilityId)
            {
                return ZoneErrors.NotFound(command.FacilityId, command.ZoneId);
            }

            if (ConcurrencyCheck.Verify(command.ExpectedVersion, zone.Version) is { } stale)
            {
                return stale;
            }

            if (zone.Rename(command.Name) && (await unitOfWork.SaveChangesAsync(cancellationToken)).Error is { } error)
            {
                return error;
            }

            return ZoneView.From(zone);
        }
    }
}
