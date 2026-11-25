using SmartParking.Application.Abstractions;
using SmartParking.Application.Abstractions.Messaging;
using SmartParking.Application.Abstractions.Persistence;
using SmartParking.Application.Abstractions.Reading;
using SmartParking.Domain.Common;
using SmartParking.Domain.Facilities;

namespace SmartParking.Application.Features.Facilities;

/// <summary>
/// Replaces the editable fields of a facility, including the whole week of opening hours (PUT).
/// Needs the version the client read: none is 428, a stale one is 412 (AC-12). The operator never changes.
/// A request that changes nothing writes nothing (no audit entry, no event) and keeps the ETag.
/// </summary>
public static class UpdateFacility
{
    public sealed record Command(
        Guid Id,
        uint? ExpectedVersion,
        string Name,
        string Address,
        double Latitude,
        double Longitude,
        string TimeZone,
        FacilityStatus Status,
        IReadOnlyList<OpeningHoursInput> OpeningHours);

    internal sealed class Handler(IParkingFacilityRepository facilities, IUnitOfWork unitOfWork)
        : ICommandHandler<Command, FacilityView>
    {
        public async Task<Result<FacilityView>> HandleAsync(Command command, CancellationToken cancellationToken)
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

            var changed = facility.Update(
                command.Name,
                command.Address,
                new GeoCoordinate(command.Latitude, command.Longitude),
                command.TimeZone,
                command.Status,
                command.OpeningHours.Select(hours => hours.ToDomain()));

            if (changed && (await unitOfWork.SaveChangesAsync(cancellationToken)).Error is { } error)
            {
                return error;
            }

            return FacilityView.From(facility);
        }
    }
}
