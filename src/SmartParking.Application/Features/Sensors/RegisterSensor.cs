using SmartParking.Application.Abstractions;
using SmartParking.Application.Abstractions.Messaging;
using SmartParking.Application.Abstractions.Persistence;
using SmartParking.Application.Abstractions.Reading;
using SmartParking.Application.Features.Facilities;
using SmartParking.Domain.Facilities;

namespace SmartParking.Application.Features.Sensors;

/// <summary>
/// Registers a sensor device (RF-06, UC-05). 404 for an unknown facility; 400 for a spot or zone of another facility
/// or an environmental sensor with a spot (AC-14); 409 for a repeated hardware id (AC-11) or a spot that already has a sensor.
/// </summary>
public static class RegisterSensor
{
    public sealed record Command(
        Guid FacilityId, string HardwareId, SensorKind Kind, string Model, DateTimeOffset InstalledAt, Guid? SpotId, Guid? ZoneId);

    internal sealed class Handler(
        IParkingFacilityRepository facilities,
        IParkingSpotRepository spots,
        IZoneRepository zones,
        ISensorRepository sensors,
        IUnitOfWork unitOfWork,
        TimeProvider clock) : ICommandHandler<Command, SensorView>
    {
        public async Task<Result<SensorView>> HandleAsync(Command command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            if (command.Kind != SensorKind.Occupancy && command.SpotId is not null)
            {
                return SensorErrors.NotOccupancy;
            }

            if (!await facilities.ExistsAsync(command.FacilityId, cancellationToken))
            {
                return FacilityErrors.NotFound(command.FacilityId);
            }

            if (await SensorPlacement.CheckSpotAsync(spots, command.FacilityId, command.SpotId, cancellationToken) is { } spotError)
            {
                return spotError;
            }

            if (await SensorPlacement.CheckZoneAsync(zones, command.FacilityId, command.ZoneId, cancellationToken) is { } zoneError)
            {
                return zoneError;
            }

            var sensor = Sensor.Create(
                Guid.CreateVersion7(clock.GetUtcNow()), command.FacilityId, command.HardwareId, command.Kind, command.Model,
                command.InstalledAt, command.SpotId, command.ZoneId);
            sensors.Add(sensor);

            var saved = await unitOfWork.SaveChangesAsync(cancellationToken);
            return saved.Error is { } error ? error : SensorView.From(sensor);
        }
    }
}
