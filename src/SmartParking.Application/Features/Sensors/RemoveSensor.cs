using SmartParking.Application.Abstractions;
using SmartParking.Application.Abstractions.Messaging;
using SmartParking.Application.Abstractions.Persistence;
using SmartParking.Application.Abstractions.Reading;

namespace SmartParking.Application.Features.Sensors;

/// <summary>Removes a sensor from the registry. A sensor still on a spot is 409: take it off first (spec 001).</summary>
public static class RemoveSensor
{
    public sealed record Command(Guid Id, uint? ExpectedVersion);

    internal sealed class Handler(ISensorRepository sensors, IUnitOfWork unitOfWork) : ICommandHandler<Command, Guid>
    {
        public async Task<Result<Guid>> HandleAsync(Command command, CancellationToken cancellationToken)
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

            if (sensor.SpotId is not null)
            {
                return SensorErrors.StillOnSpot;
            }

            sensor.MarkForRemoval();
            sensors.Remove(sensor);

            var saved = await unitOfWork.SaveChangesAsync(cancellationToken);
            return saved.Error is { } error ? error : sensor.Id;
        }
    }
}
