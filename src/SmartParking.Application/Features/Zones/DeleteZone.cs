using SmartParking.Application.Abstractions;
using SmartParking.Application.Abstractions.Messaging;
using SmartParking.Application.Abstractions.Persistence;
using SmartParking.Application.Abstractions.Reading;

namespace SmartParking.Application.Features.Zones;

/// <summary>
/// Deletes a zone. While spots or sensors point at it, the database refuses it (FK RESTRICT) and the whole
/// transaction, audit and outbox included, is rolled back (AC-13, 409).
/// </summary>
public static class DeleteZone
{
    public sealed record Command(Guid FacilityId, Guid ZoneId, uint? ExpectedVersion);

    internal sealed class Handler(IZoneRepository zones, IUnitOfWork unitOfWork) : ICommandHandler<Command, Guid>
    {
        public async Task<Result<Guid>> HandleAsync(Command command, CancellationToken cancellationToken)
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

            zone.MarkForDeletion();
            zones.Remove(zone);

            var saved = await unitOfWork.SaveChangesAsync(cancellationToken);
            return saved.Error is { } error ? error : zone.Id;
        }
    }
}
