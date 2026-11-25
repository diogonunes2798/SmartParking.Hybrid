using Microsoft.EntityFrameworkCore;
using SmartParking.Application.Abstractions;
using SmartParking.Application.Abstractions.Reading;
using SmartParking.Domain.Facilities;

namespace SmartParking.Infrastructure.Relational.Reading;

/// <summary>
/// The ingestion's registry check over PostgreSQL (spec 003): one compiled, <c>AsNoTracking</c> query per batch with
/// all its hardware ids (<c>= ANY</c>), returning each sensor with its facility and, when it has one, its spot. No cache,
/// so a configuration change applies to the next batch. A database failure becomes <see cref="ErrorType.Unavailable"/>.
/// </summary>
internal sealed class SensorRegistryReader(SmartParkingDbContext db) : ISensorRegistryReader
{
    private static readonly Func<SmartParkingDbContext, string[], IAsyncEnumerable<SensorRegistration>> ByHardwareIds =
        EF.CompileAsyncQuery((SmartParkingDbContext context, string[] hardwareIds) =>
            from sensor in context.Sensors.AsNoTracking()
            where hardwareIds.Contains(sensor.HardwareId)
            join facility in context.ParkingFacilities.AsNoTracking() on sensor.FacilityId equals facility.Id
            from spot in context.ParkingSpots.AsNoTracking().Where(spot => spot.Id == sensor.SpotId).DefaultIfEmpty()
            select new SensorRegistration(
                sensor.Id,
                sensor.HardwareId,
                sensor.Kind,
                sensor.Status,
                sensor.FacilityId,
                facility.Status,
                sensor.SpotId,
                spot == null ? null : spot.ZoneId,
                spot == null ? null : spot.Status));

    public async Task<Result<IReadOnlyList<SensorRegistration>>> GetByHardwareIdsAsync(
        IReadOnlyCollection<string> hardwareIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(hardwareIds);

        try
        {
            List<SensorRegistration> registrations = [];
            await foreach (var registration in ByHardwareIds(db, [.. hardwareIds]).WithCancellation(cancellationToken))
            {
                registrations.Add(registration);
            }

            return registrations;
        }
        catch (Exception exception) when (PostgresUnavailability.IsUnavailable(exception))
        {
            return Error.Unavailable("Ingestion.RegistryUnavailable", "The sensor registry could not be read. Retry later.");
        }
    }
}
