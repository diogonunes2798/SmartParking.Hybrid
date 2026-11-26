namespace SmartParking.SensorSimulator;

/// <summary>Exit codes of the simulator. Parse errors are 1 (System.CommandLine).</summary>
internal static class ExitCodes
{
    public const int Success = 0;

    public const int InvalidOptions = 2;

    public const int ProvisioningFailed = 3;

    public const int MappingNotFound = 4;

    public const int NotAllDelivered = 5;

    /// <summary>
    /// Ctrl+C. Takes precedence over <see cref="NotAllDelivered"/>: a cancelled run is not a measurement, so whether
    /// its drain delivered everything does not matter to the caller (the summary in the log still says).
    /// </summary>
    public const int Cancelled = 130;
}
