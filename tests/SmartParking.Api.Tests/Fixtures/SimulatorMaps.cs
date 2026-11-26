using SmartParking.SensorSimulator.Provisioning;

namespace SmartParking.Api.Tests.Fixtures;

/// <summary>Mappings for the simulator's tests (spec 003, F4b): synthetic ones for the generator, or one built from a registered site.</summary>
internal static class SimulatorMaps
{
    private static readonly Guid FacilityId = new("0193a1b0-0000-7000-8000-00000000f4b0");

    private static readonly Guid ZoneId = new("0193a1b0-0000-7000-8000-00000000f4b1");

    /// <summary>A mapping of <paramref name="spots"/> sensors that exists only in memory (the generator never calls the Api).</summary>
    public static IReadOnlyList<SensorMapEntry> Synthetic(int spots) =>
    [
        .. Enumerable.Range(1, spots).Select(index => new SensorMapEntry(
            $"TEST-{index:D5}", new Guid(index, 0, 0, new byte[8]), ZoneId, FacilityId)),
    ];

    /// <summary>The mapping of a site registered through the F2 endpoints (<see cref="IngestionApi.CreateSiteAsync"/>).</summary>
    public static SensorMap FromSite(IngestionApi.Site site) => new(
        0,
        site.Facility.Id,
        [site.Zone.Id],
        [.. site.Spots.Select(spot => new SensorMapEntry(spot.HardwareId, spot.SpotId, site.Zone.Id, site.Facility.Id))]);
}
