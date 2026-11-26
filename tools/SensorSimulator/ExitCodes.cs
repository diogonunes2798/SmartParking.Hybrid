namespace SmartParking.SensorSimulator;

/// <summary>Exit codes of the simulator. Parse errors are 1 (System.CommandLine).</summary>
internal static class ExitCodes
{
    public const int Success = 0;

    public const int InvalidOptions = 2;

    public const int ProvisioningFailed = 3;

    public const int MappingNotFound = 4;

    public const int NotAllDelivered = 5;

    public const int Cancelled = 130;
}
