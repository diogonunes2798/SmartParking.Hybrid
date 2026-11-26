using System.Globalization;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using SmartParking.SensorSimulator.Wire;

namespace SmartParking.SensorSimulator.Provisioning;

/// <summary>
/// <c>--provision</c> (spec 003, round 5): creates one facility, its zones, and the spots with their occupancy sensors
/// only through the F2 endpoints (no SQL), and returns the <c>hardwareId</c>–<c>spotId</c> mapping. The layout is a
/// function of the settings: the facility is named after the seed, zone <c>z</c> is <c>Z{z}</c>, spot <c>i</c> (1-based)
/// has the code <c>Z{z}-{i:D5}</c> and its sensor the <c>hardwareId</c> <c>SIM-{seed}-{i:D5}</c>, and the spots are
/// split across the zones in order. Only the ids differ between two provisionings with the same seed.
/// Each zone's spots go in requests of at most 1 000 (<c>spots/batch</c>, all or nothing per request).
/// </summary>
internal sealed partial class Provisioner(IHttpClientFactory httpClientFactory, TimeProvider clock, ILogger<Provisioner> logger)
{
    /// <summary>The reference operator created by the F2 migrations (spec 001).</summary>
    public static readonly Guid ReferenceOperatorId = new("0193a1b0-0000-7000-8000-000000000001");

    public const string SensorModel = "SensorSimulator";

    public static string FacilityName(int seed) => string.Create(CultureInfo.InvariantCulture, $"Simulator seed {seed}");

    public static string HardwareId(int seed, int spot) => string.Create(CultureInfo.InvariantCulture, $"SIM-{seed}-{spot:D5}");

    public async Task<ProvisionOutcome> ProvisionAsync(ProvisionSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var client = httpClientFactory.CreateClient(ApiWire.ClientName);
        var installedAt = clock.GetUtcNow();

        var facility = await PostAsync<CreatedResource>(client, ApiWire.FacilitiesRoute, NewFacility(settings.Seed), cancellationToken);
        if (facility.Error is not null)
        {
            return ProvisionOutcome.Failure(facility.Error);
        }

        var facilityId = facility.Value!.Id;
        var zoneIds = new List<Guid>(settings.Zones);
        var sensors = new List<SensorMapEntry>(settings.Spots);

        for (var zone = 0; zone < settings.Zones; zone++)
        {
            var created = await PostAsync<CreatedResource>(
                client, ApiWire.Zones(facilityId), new ZoneRequest(ZoneName(zone)), cancellationToken);
            if (created.Error is not null)
            {
                return ProvisionOutcome.Failure(created.Error);
            }

            var zoneId = created.Value!.Id;
            zoneIds.Add(zoneId);

            var first = zone * settings.Spots / settings.Zones;
            var last = (zone + 1) * settings.Spots / settings.Zones;
            for (var chunk = first; chunk < last; chunk += SpotBatchRequest.MaxItems)
            {
                var spots = Enumerable.Range(chunk + 1, Math.Min(SpotBatchRequest.MaxItems, last - chunk))
                    .Select(spot => new SpotBatchItemRequest(
                        zoneId,
                        string.Create(CultureInfo.InvariantCulture, $"{ZoneName(zone)}-{spot:D5}"),
                        "Standard",
                        new SpotBatchSensorRequest(HardwareId(settings.Seed, spot), SensorModel, installedAt)))
                    .ToList();

                var batch = await PostAsync<SpotBatchResponse>(
                    client, ApiWire.SpotBatch(facilityId), new SpotBatchRequest(spots), cancellationToken);
                if (batch.Error is not null)
                {
                    return ProvisionOutcome.Failure(batch.Error);
                }

                sensors.AddRange(batch.Value!.Items.Select((item, index) =>
                    new SensorMapEntry(spots[index].Sensor.HardwareId, item.SpotId, zoneId, facilityId)));
            }
        }

        LogProvisioned(logger, facilityId, zoneIds.Count, sensors.Count, settings.Seed);
        return ProvisionOutcome.Success(new SensorMap(settings.Seed, facilityId, zoneIds, sensors));
    }

    private static string ZoneName(int zone) => string.Create(CultureInfo.InvariantCulture, $"Z{zone + 1}");

    private static CreateFacilityRequest NewFacility(int seed) => new(
        ReferenceOperatorId,
        FacilityName(seed),
        "Simulated facility (SensorSimulator)",
        40.1925,
        -8.4115,
        "Europe/Lisbon",
        [.. Enumerable.Range(0, 7).Select(day => new OpeningHoursRequest(day, TimeOnly.MinValue, new TimeOnly(23, 59, 59)))]);

    private static async Task<(TResponse? Value, string? Error)> PostAsync<TResponse>(
        HttpClient client, string route, object body, CancellationToken cancellationToken)
    {
        using var response = await client.PostAsJsonAsync(route, body, ApiWire.Json, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var problem = await response.Content.ReadAsStringAsync(cancellationToken);
            return (default, string.Create(CultureInfo.InvariantCulture, $"POST {route} returned {(int)response.StatusCode}: {problem}"));
        }

        var value = await response.Content.ReadFromJsonAsync<TResponse>(ApiWire.Json, cancellationToken);
        return value is null
            ? (default, string.Create(CultureInfo.InvariantCulture, $"POST {route} returned an empty body"))
            : (value, null);
    }

    [LoggerMessage(EventId = 20, Level = LogLevel.Information,
        Message = "Provisioned facility {FacilityId}: {Zones} zones, {Sensors} spots with sensors (seed {Seed})")]
    private static partial void LogProvisioned(ILogger logger, Guid facilityId, int zones, int sensors, int seed);
}
