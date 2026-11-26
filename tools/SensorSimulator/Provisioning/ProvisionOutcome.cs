namespace SmartParking.SensorSimulator.Provisioning;

/// <summary>Either the mapping of a completed provisioning or the reason it stopped (an Api answer that is not a success).</summary>
internal sealed record ProvisionOutcome(SensorMap? Map, string? Error)
{
    public static ProvisionOutcome Success(SensorMap map) => new(map, null);

    public static ProvisionOutcome Failure(string error) => new(null, error);
}
