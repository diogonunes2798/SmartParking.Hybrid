using SmartParking.Application.Abstractions;
using SmartParking.Application.Abstractions.Messaging;
using SmartParking.Application.Abstractions.Persistence;

namespace SmartParking.Application.Features.Facilities;

/// <summary>
/// Deletes a facility; its opening hours go with it (cascade). While zones, spots, sensors or tariffs exist,
/// the database refuses it (FK RESTRICT) and the whole transaction, audit and outbox included, is rolled back
/// (AC-5, 409). Returns the id of the deleted facility.
/// </summary>
public static class DeleteFacility
{
    public sealed record Command(Guid Id, uint? ExpectedVersion);

    internal sealed class Handler(IParkingFacilityRepository facilities, IUnitOfWork unitOfWork)
        : ICommandHandler<Command, Guid>
    {
        public async Task<Result<Guid>> HandleAsync(Command command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            if (command.ExpectedVersion is null)
            {
                return ConcurrencyCheck.VersionRequired;
            }

            var facility = await facilities.GetByIdAsync(command.Id, cancellationToken);
            if (facility is null)
            {
                return FacilityErrors.NotFound(command.Id);
            }

            if (ConcurrencyCheck.Verify(command.ExpectedVersion, facility.Version) is { } stale)
            {
                return stale;
            }

            facility.MarkForDeletion();
            facilities.Remove(facility);

            var saved = await unitOfWork.SaveChangesAsync(cancellationToken);
            if (saved.Error is { } error)
            {
                return error;
            }

            return facility.Id;
        }
    }
}
