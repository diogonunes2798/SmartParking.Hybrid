using System.Diagnostics;

namespace SmartParking.Application.Diagnostics;

/// <summary>
/// Names of the custom OpenTelemetry sources used by the prototype.
/// Kept in the Application layer (System.Diagnostics only, no OpenTelemetry package)
/// so that use-case handlers can emit spans and metrics without depending on hosting code.
/// The hosts register these names in SmartParking.ServiceDefaults.
/// </summary>
public static class SmartParkingTelemetry
{
    /// <summary>Value of the OpenTelemetry <c>service.namespace</c> resource attribute.</summary>
    public const string ServiceNamespace = "smartparking";

    /// <summary>Name of the custom <see cref="ActivitySource"/> (traces).</summary>
    public const string ActivitySourceName = "SmartParking";

    /// <summary>Name of the custom <c>Meter</c> (metrics). Instruments are created through <c>IMeterFactory</c>.</summary>
    public const string MeterName = "SmartParking";

    /// <summary>Shared activity source for spans emitted by application handlers.</summary>
    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);
}
