using SmartParking.Application.Abstractions;
using SmartParking.Application.Abstractions.Messaging;
using SmartParking.Application.Abstractions.Persistence;
using SmartParking.Application.Abstractions.Reading;
using SmartParking.Domain.Common;
using SmartParking.Domain.Facilities;

namespace SmartParking.Application.Features.Facilities;

/// <summary>
/// Registers a parking facility with its opening hours (RF-06). The unit of work writes the facility,
/// one audit entry and one <c>FacilityCreated.v1</c> outbox message in one transaction (AC-4).
/// A repeated name for the operator, or an unknown operator, comes back from the database as 409.
/// </summary>
public static class CreateFacility
{
    public sealed record Command(
        Guid OperatorId,
        string Name,
        string Address,
        double Latitude,
        double Longitude,
        string TimeZone,
        IReadOnlyList<OpeningHoursInput> OpeningHours);

    internal sealed class Handler(IParkingFacilityRepository facilities, IUnitOfWork unitOfWork, TimeProvider clock)
        : ICommandHandler<Command, FacilityView>
    {
        public async Task<Result<FacilityView>> HandleAsync(Command command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            var facility = ParkingFacility.Create(
                Guid.CreateVersion7(clock.GetUtcNow()),
                command.OperatorId,
                command.Name,
                command.Address,
                new GeoCoordinate(command.Latitude, command.Longitude),
                command.TimeZone,
                command.OpeningHours.Select(hours => hours.ToDomain()));

            facilities.Add(facility);

            var saved = await unitOfWork.SaveChangesAsync(cancellationToken);
            if (saved.Error is { } error)
            {
                return error;
            }

            return FacilityView.From(facility);
        }
    }
}
