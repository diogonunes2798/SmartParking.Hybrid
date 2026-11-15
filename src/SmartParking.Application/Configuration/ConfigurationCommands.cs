using SmartParking.Application.Abstractions;
using SmartParking.Application.Common;
using SmartParking.Domain.Common;
using SmartParking.Domain.Facilities;
using SmartParking.Domain.Tariffs;

namespace SmartParking.Application.Configuration;

// Command side of RF-06 (CQRS, ADR-0001: plain handler classes, no mediator). One handler per resource,
// each with its create/update/delete commands; reads go through IConfigurationQueries.
// TODO(Phase 2b/5): relevant changes must emit events for the read model (spec §4, RF-06) through the outbox.

internal static class Concurrency // shared with the UC-02 handlers (same assembly)
{
    /// <summary>
    /// Rejects an update based on a stale read. A change made between this check and the commit is still
    /// caught by the store's concurrency token (<see cref="IUnitOfWork.SaveChangesAsync"/>).
    /// </summary>
    public static void EnsureVersion(IVersioned entity, uint expected, string resource)
    {
        if (entity.Version != expected)
        {
            throw new ConcurrencyConflictException(
                $"{resource} was modified by another request (expected version {expected}, current {entity.Version}).");
        }
    }
}

public sealed class FacilityCommands(IRepository<ParkingFacility> facilities, IUnitOfWork unitOfWork)
{
    private const string Resource = "Facility";

    public async Task<FacilityDto> CreateAsync(CreateFacility command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var facility = ParkingFacility.Create(
            command.Name, RequireLocation(command.Location), command.Capacity, command.Type, command.Operator, command.Zone);
        facilities.Add(facility);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return FacilityDto.From(facility);
    }

    public async Task<FacilityDto> UpdateAsync(Guid id, UpdateFacility command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var facility = await facilities.GetAsync(id, cancellationToken).ConfigureAwait(false) ?? throw new NotFoundException(Resource, id);
        Concurrency.EnsureVersion(facility, command.Version, Resource);
        facility.Update(
            command.Name, RequireLocation(command.Location), command.Capacity, command.Type, command.Operator, command.Zone);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return FacilityDto.From(facility);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var facility = await facilities.GetAsync(id, cancellationToken).ConfigureAwait(false) ?? throw new NotFoundException(Resource, id);
        facilities.Remove(facility);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static GeoPoint RequireLocation(GeoJsonPoint? location) =>
        location?.ToGeoPoint() ?? throw new DomainValidationException("location", "is required");
}

public sealed class SpotCommands(
    IRepository<ParkingSpot> spots, IRepository<ParkingFacility> facilities, IUnitOfWork unitOfWork)
{
    private const string Resource = "Spot";

    public async Task<SpotDto> CreateAsync(Guid facilityId, CreateSpot command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!await facilities.ExistsAsync(facilityId, cancellationToken).ConfigureAwait(false))
        {
            throw new NotFoundException("Facility", facilityId);
        }

        var spot = ParkingSpot.Create(facilityId, command.Code, command.AllowedVehicleType);
        spots.Add(spot);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return SpotDto.From(spot);
    }

    public async Task<SpotDto> UpdateAsync(Guid id, UpdateSpot command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var spot = await spots.GetAsync(id, cancellationToken).ConfigureAwait(false) ?? throw new NotFoundException(Resource, id);
        Concurrency.EnsureVersion(spot, command.Version, Resource);
        spot.Update(command.Code, command.AllowedVehicleType);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return SpotDto.From(spot);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var spot = await spots.GetAsync(id, cancellationToken).ConfigureAwait(false) ?? throw new NotFoundException(Resource, id);
        spots.Remove(spot);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}

public sealed class SensorCommands(
    IRepository<Sensor> sensors, IRepository<ParkingSpot> spots, IUnitOfWork unitOfWork)
{
    private const string Resource = "Sensor";

    public async Task<SensorDto> CreateAsync(CreateSensor command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await EnsureSpotExistsAsync(command.SpotId, cancellationToken).ConfigureAwait(false);
        var sensor = Sensor.Create(command.SpotId, command.Model, command.FirmwareVersion, command.Status);
        sensors.Add(sensor);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return SensorDto.From(sensor);
    }

    public async Task<SensorDto> UpdateAsync(Guid id, UpdateSensor command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var sensor = await sensors.GetAsync(id, cancellationToken).ConfigureAwait(false) ?? throw new NotFoundException(Resource, id);
        Concurrency.EnsureVersion(sensor, command.Version, Resource);
        if (command.SpotId != sensor.SpotId)
        {
            await EnsureSpotExistsAsync(command.SpotId, cancellationToken).ConfigureAwait(false);
        }

        sensor.Update(command.SpotId, command.Model, command.FirmwareVersion, command.Status);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return SensorDto.From(sensor);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var sensor = await sensors.GetAsync(id, cancellationToken).ConfigureAwait(false) ?? throw new NotFoundException(Resource, id);
        sensors.Remove(sensor);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureSpotExistsAsync(Guid spotId, CancellationToken cancellationToken)
    {
        if (spotId == Guid.Empty)
        {
            throw new DomainValidationException("spotId", "is required");
        }

        if (!await spots.ExistsAsync(spotId, cancellationToken).ConfigureAwait(false))
        {
            throw new NotFoundException("Spot", spotId);
        }
    }
}

public sealed class TariffCommands(IRepository<Tariff> tariffs, IUnitOfWork unitOfWork)
{
    private const string Resource = "Tariff";

    public async Task<TariffDto> CreateAsync(CreateTariff command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var tariff = Tariff.Create(
            command.Zone, new ValidityPeriod(command.ValidFrom, command.ValidTo), RequireRules(command.Rules));
        tariffs.Add(tariff);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return TariffDto.From(tariff);
    }

    public async Task<TariffDto> UpdateAsync(Guid id, UpdateTariff command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var tariff = await tariffs.GetAsync(id, cancellationToken).ConfigureAwait(false) ?? throw new NotFoundException(Resource, id);
        Concurrency.EnsureVersion(tariff, command.Version, Resource);
        tariff.Update(command.Zone, new ValidityPeriod(command.ValidFrom, command.ValidTo), RequireRules(command.Rules));
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return TariffDto.From(tariff);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var tariff = await tariffs.GetAsync(id, cancellationToken).ConfigureAwait(false) ?? throw new NotFoundException(Resource, id);
        tariffs.Remove(tariff);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static TariffRules RequireRules(TariffRulesDto? rules) =>
        rules?.ToDomain() ?? throw new DomainValidationException("rules", "is required");
}
