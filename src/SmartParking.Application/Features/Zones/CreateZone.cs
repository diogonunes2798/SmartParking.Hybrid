using SmartParking.Application.Abstractions;
using SmartParking.Application.Abstractions.Messaging;
using SmartParking.Application.Abstractions.Persistence;
using SmartParking.Application.Abstractions.Reading;
using SmartParking.Application.Features.Facilities;
using SmartParking.Domain.Facilities;

namespace SmartParking.Application.Features.Zones;

/// <summary>
/// Adds a zone to a facility (RF-06): 404 when the facility does not exist; a repeated name in the facility is 409
/// (unique index). The unit of work writes the zone, one audit entry and one <c>ZoneCreated.v1</c> in one transaction.
/// </summary>
public static class CreateZone
{
    public sealed record Command(Guid FacilityId, string Name);

    internal sealed class Handler(
        IParkingFacilityRepository facilities,
        IZoneRepository zones,
        IUnitOfWork unitOfWork,
        TimeProvider clock) : ICommandHandler<Command, ZoneView>
    {
        public async Task<Result<ZoneView>> HandleAsync(Command command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            if (!await facilities.ExistsAsync(command.FacilityId, cancellationToken))
            {
                return FacilityErrors.NotFound(command.FacilityId);
            }

            var zone = Zone.Create(Guid.CreateVersion7(clock.GetUtcNow()), command.FacilityId, command.Name);
            zones.Add(zone);

            var saved = await unitOfWork.SaveChangesAsync(cancellationToken);
            return saved.Error is { } error ? error : ZoneView.From(zone);
        }
    }
}
