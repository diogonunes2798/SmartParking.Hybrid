using System.Text.Json;

namespace SmartParking.SensorSimulator.Wire;

/// <summary>
/// JSON settings of the Api's wire format (camelCase, as ASP.NET Core). The simulator keeps its own records of the
/// requests it sends: it is a black-box client of the Api and references no project of <c>src/</c> (spec 003, AC-19).
/// </summary>
internal static class ApiWire
{
    public const string ClientName = "SmartParkingApi";

    public const string FacilitiesRoute = "/api/facilities";

    public const string IngestRoute = "/api/ingest/occupancy-events";

    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web);

    public static string Zones(Guid facilityId) => $"{FacilitiesRoute}/{facilityId}/zones";

    public static string SpotBatch(Guid facilityId) => $"{FacilitiesRoute}/{facilityId}/spots/batch";
}
