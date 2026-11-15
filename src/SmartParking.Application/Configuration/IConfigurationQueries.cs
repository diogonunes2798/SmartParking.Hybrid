namespace SmartParking.Application.Configuration;

/// <summary>
/// Query side of RF-06 (CQRS): read-only projections straight to DTOs, without loading aggregates
/// for modification. Implemented by the relational sub-layer (no-tracking queries).
/// </summary>
public interface IConfigurationQueries
{
    Task<FacilityDto?> GetFacilityAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<FacilityDto>> ListFacilitiesAsync(PageRequest page, CancellationToken cancellationToken);

    Task<SpotDto?> GetSpotAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<SpotDto>> ListSpotsAsync(Guid facilityId, PageRequest page, CancellationToken cancellationToken);

    Task<SensorDto?> GetSensorAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<SensorDto>> ListSensorsAsync(Guid? spotId, PageRequest page, CancellationToken cancellationToken);

    Task<TariffDto?> GetTariffAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<TariffDto>> ListTariffsAsync(string? zone, PageRequest page, CancellationToken cancellationToken);
}
