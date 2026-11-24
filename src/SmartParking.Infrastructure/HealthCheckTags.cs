namespace SmartParking.Infrastructure;

/// <summary>Tags shared by the sublayer health checks and the <c>/health/ready</c> endpoint.</summary>
public static class HealthCheckTags
{
    /// <summary>Checks that must pass before the host is ready to serve traffic.</summary>
    public const string Ready = "ready";

    /// <summary>Configuration key for the per-check timeout (a <see cref="TimeSpan"/>).</summary>
    public const string TimeoutKey = "HealthChecks:Timeout";

    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);
}
