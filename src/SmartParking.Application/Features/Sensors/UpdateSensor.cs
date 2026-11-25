using SmartParking.Application.Abstractions;
using SmartParking.Application.Abstractions.Messaging;
using SmartParking.Application.Abstractions.Persistence;
using SmartParking.Application.Abstractions.Reading;
using SmartParking.Domain.Facilities;

namespace SmartParking.Application.Features.Sensors;

/// <summary>
/// Replaces a sensor's model, status and zone (PUT); 428/412 for a missing or stale version; 400 for a zone of another
/// facility. The spot changes only through <c>PUT /sensors/{id}/spot</c>. A request that changes nothing writes nothing.
/// </summary>
public static class UpdateSensor
{
    public sealed record Command(Guid Id, uint? ExpectedVersion, string Model, SensorStatus Status, Guid? ZoneId);

    internal sealed class Handler(ISensorRepository sensors, IZoneRepository zones, IUnitOfWork unitOfWork)
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

            if (command.ZoneId != sensor.ZoneId
                && await SensorPlacement.CheckZoneAsync(zones, sensor.FacilityId, command.ZoneId, cancellationToken) is { } zoneError)
            {
                return zoneError;
            }

            var changed = sensor.Update(command.Model, command.Status, command.ZoneId);
            if (changed && (await unitOfWork.SaveChangesAsync(cancellationToken)).Error is { } error)
            {
                return error;
            }

            return SensorView.From(sensor);
        }
    }
}
