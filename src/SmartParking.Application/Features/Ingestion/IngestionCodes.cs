namespace SmartParking.Application.Features.Ingestion;

/// <summary>
/// Rejection codes of the registry rules, in the order they are checked (spec 003, «Validação contra o registo»).
/// The first rule that fails gives the code.
/// </summary>
public static class IngestionCodes
{
    public const string SensorUnknown = "sensor_unknown";
    public const string SensorInactive = "sensor_inactive";
    public const string SensorNotOccupancy = "sensor_not_occupancy";
    public const string SensorUnassigned = "sensor_unassigned";
    public const string SpotMismatch = "spot_mismatch";
    public const string SpotInactive = "spot_inactive";
    public const string FacilityInactive = "facility_inactive";
    public const string TimestampInFuture = "timestamp_in_future";
}
