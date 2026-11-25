using SmartParking.Application.Abstractions;
using SmartParking.Application.Abstractions.Messaging;
using SmartParking.Application.Abstractions.Persistence;
using SmartParking.Application.Abstractions.Reading;

namespace SmartParking.Application.Features.Spots;

/// <summary>
/// Deletes a spot. While a sensor, reservation or session points at it, the database refuses it (FK RESTRICT) and
/// the whole transaction is rolled back (AC-13, 409).
/// </summary>
public static class DeleteSpot
{
    public sealed record Command(Guid FacilityId, Guid SpotId, uint? ExpectedVersion);

    internal sealed class Handler(IParkingSpotRepository spots, IUnitOfWork unitOfWork) : ICommandHandler<Command, Guid>
    {
        public async Task<Result<Guid>> HandleAsync(Command command, CancellationToken cancellationToken)
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

            spot.MarkForDeletion();
            spots.Remove(spot);

            var saved = await unitOfWork.SaveChangesAsync(cancellationToken);
            return saved.Error is { } error ? error : spot.Id;
        }
    }
}
