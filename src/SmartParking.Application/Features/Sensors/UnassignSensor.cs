using SmartParking.Application.Abstractions;
using SmartParking.Application.Abstractions.Messaging;
using SmartParking.Application.Abstractions.Persistence;
using SmartParking.Application.Abstractions.Reading;

namespace SmartParking.Application.Features.Sensors;

/// <summary>
/// Takes a sensor off its spot (AC-14): <c>spotId</c> becomes null and <c>SensorUnassigned.v1</c> is emitted. The sensor
/// stays registered; F4 rejects its events. A sensor without a spot changes nothing (200; plan 001b, point 6).
/// </summary>
public static class UnassignSensor
{
    public sealed record Command(Guid Id, uint? ExpectedVersion);

    internal sealed class Handler(ISensorRepository sensors, IUnitOfWork unitOfWork) : ICommandHandler<Command, SensorView>
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

            if (sensor.Unassign() && (await unitOfWork.SaveChangesAsync(cancellationToken)).Error is { } error)
            {
                return error;
            }

            return SensorView.From(sensor);
        }
    }
}
