using SmartParking.Application.Abstractions;
using SmartParking.Application.Abstractions.Messaging;
using SmartParking.Application.Abstractions.Persistence;
using SmartParking.Application.Abstractions.Reading;
using SmartParking.Application.Features.Facilities;
using SmartParking.Domain.Facilities;

namespace SmartParking.Application.Features.Spots;

/// <summary>
/// Adds 1 to 1 000 spots, each optionally with its occupancy sensor, all or nothing (AC-15). Prepares the reference
/// scenario (5 000 spots, Table 9) in five requests. The request is validated at the API boundary (size, repeated
/// codes and hardware ids); here the zones are checked with one query (400) and the rest is left to the database:
/// a code or hardware id that already exists is 409 and nothing is created. One audit entry and one outbox message
/// per spot and per sensor, in one transaction.
/// </summary>
public static class CreateSpotBatch
{
    public const int MaxItems = 1000;

    private static readonly string[] ZoneNotInFacilityMessage = ["The zone does not belong to this parking facility."];

    public sealed record SensorInput(string HardwareId, string Model, DateTimeOffset InstalledAt);

    public sealed record Item(Guid ZoneId, string Code, SpotType Type, SensorInput? Sensor);

    public sealed record Command(Guid FacilityId, IReadOnlyList<Item> Spots);

    public sealed record CreatedItem(Guid SpotId, string Code, Guid? SensorId);

    internal sealed class Handler(
        IParkingFacilityRepository facilities,
        IZoneRepository zones,
        IParkingSpotRepository spots,
        ISensorRepository sensors,
        IUnitOfWork unitOfWork,
        TimeProvider clock) : ICommandHandler<Command, IReadOnlyList<CreatedItem>>
    {
        public async Task<Result<IReadOnlyList<CreatedItem>>> HandleAsync(Command command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            if (!await facilities.ExistsAsync(command.FacilityId, cancellationToken))
            {
                return FacilityErrors.NotFound(command.FacilityId);
            }

            var facilityZones = await zones.GetIdsByFacilityAsync(command.FacilityId, cancellationToken);
            var wrongZones = command.Spots
                .Select((item, index) => (item, index))
                .Where(entry => !facilityZones.Contains(entry.item.ZoneId))
                .ToDictionary(entry => $"spots[{entry.index}].zoneId", _ => ZoneNotInFacilityMessage);
            if (wrongZones.Count > 0)
            {
                return SpotErrors.ZoneNotInFacility(wrongZones);
            }

            var now = clock.GetUtcNow();
            List<ParkingSpot> newSpots = [];
            List<Sensor> newSensors = [];
            List<CreatedItem> created = [];

            foreach (var item in command.Spots)
            {
                var spot = ParkingSpot.Create(Guid.CreateVersion7(now), command.FacilityId, item.ZoneId, item.Code, item.Type);
                newSpots.Add(spot);

                Sensor? sensor = null;
                if (item.Sensor is { } input)
                {
                    sensor = Sensor.Create(
                        Guid.CreateVersion7(now), command.FacilityId, input.HardwareId, SensorKind.Occupancy, input.Model,
                        input.InstalledAt, spot.Id, item.ZoneId);
                    newSensors.Add(sensor);
                }

                created.Add(new CreatedItem(spot.Id, spot.Code, sensor?.Id));
            }

            spots.AddRange(newSpots);
            sensors.AddRange(newSensors);

            var saved = await unitOfWork.SaveChangesAsync(cancellationToken);
            return saved.Error is { } error ? error : Result.Success<IReadOnlyList<CreatedItem>>(created);
        }
    }
}
