using SmartParking.Application.Abstractions;
using SmartParking.Application.Abstractions.Messaging;
using SmartParking.Application.Abstractions.Persistence;
using SmartParking.Application.Abstractions.Reading;
using SmartParking.Domain.Facilities;

namespace SmartParking.Application.Features.Sensors;

/// <summary>
/// Places an occupancy sensor on a spot of its facility (AC-14): 400 for an environmental sensor or a spot of another
/// facility; 409 when the spot already has a sensor (unique index). The same spot again changes nothing (200, no audit,
/// no event). <c>SensorReassigned.v1</c> carries the previous spot.
/// </summary>
public static class AssignSensorToSpot
{
    public sealed record Command(Guid Id, uint? ExpectedVersion, Guid SpotId);

    internal sealed class Handler(ISensorRepository sensors, IParkingSpotRepository spots, IUnitOfWork unitOfWork)
        : ICommandHandler<Command, SensorView>
    {
        public async Task<Result<SensorView>> HandleAsync(Command command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            if (command.ExpectedVersion is null)
            {
                return ConcurrencyCheck.VersionRequired;
            }

            var sensor = await sensors.GetByIdAsync(command.Id, cancellationToken);
            if (sensor is null)
            {
                return SensorErrors.NotFound(command.Id);
            }

            if (ConcurrencyCheck.Verify(command.ExpectedVersion, sensor.Version) is { } stale)
            {
                return stale;
            }

            if (sensor.Kind != SensorKind.Occupancy)
            {
                return SensorErrors.NotOccupancy;
            }

            if (sensor.SpotId != command.SpotId
                && await SensorPlacement.CheckSpotAsync(spots, sensor.FacilityId, command.SpotId, cancellationToken) is { } spotError)
            {
                return spotError;
            }

            if (sensor.AssignTo(command.SpotId) && (await unitOfWork.SaveChangesAsync(cancellationToken)).Error is { } error)
            {
                return error;
            }

            return SensorView.From(sensor);
        }
    }
}
