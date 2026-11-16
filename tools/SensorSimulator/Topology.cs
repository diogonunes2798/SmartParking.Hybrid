using System.Globalization;

namespace SmartParking.SensorSimulator;

/// <summary>
/// Ids of the simulated facilities, spots and sensors, as assigned by the API (RF-06). Index <c>i</c> of
/// <see cref="SpotIds"/> and <see cref="SensorIds"/> is spot <c>i</c> of the generator.
/// </summary>
internal sealed record Topology(Guid[] FacilityIds, Guid[] SpotIds, Guid[] SensorIds, int[] FacilityOfSpot)
{
    /// <summary>Spots are split among facilities in contiguous blocks; the first <c>spots % facilities</c> get one more.</summary>
    public static int[] MapSpotsToFacilities(int facilities, int spots)
    {
        var result = new int[spots];
        var index = 0;
        for (var f = 0; f < facilities; f++)
        {
            var count = SpotsIn(f, facilities, spots);
            for (var j = 0; j < count; j++)
            {
                result[index++] = f;
            }
        }

        return result;
    }

    public static int SpotsIn(int facility, int facilities, int spots) =>
        (spots / facilities) + (facility < spots % facilities ? 1 : 0);

    // Names are deterministic, so that a second run of the same scenario finds and reuses what the first created.

    public static string FacilityName(string scenario, int facility) =>
        string.Create(CultureInfo.InvariantCulture, $"sim-{scenario}-F{facility:000}");

    public static string SpotCode(int indexInFacility) =>
        string.Create(CultureInfo.InvariantCulture, $"SIM-{indexInFacility:00000}");

    /// <summary>One analytics zone per facility, so that UC-04 reports can be checked per facility.</summary>
    public static string Zone(string scenario, int facility) =>
        string.Create(CultureInfo.InvariantCulture, $"sim-{scenario}-Z{facility:000}");

    /// <summary>
    /// Facilities on a grid around Coimbra (≈ 40.2033, -8.4103), about 300 m apart, so that a small availability
    /// radius around one facility finds only that facility.
    /// </summary>
    public static (double Latitude, double Longitude) Location(int facility)
    {
        const int columns = 20;
        const double step = 0.003; // ≈ 330 m in latitude, ≈ 255 m in longitude at 40° N
        return (40.2033 + ((facility / columns) * step), -8.4103 + ((facility % columns) * step));
    }
}
