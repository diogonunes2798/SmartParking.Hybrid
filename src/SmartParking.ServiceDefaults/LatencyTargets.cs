using System.Diagnostics.Metrics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SmartParking.Application.Diagnostics;

namespace SmartParking.ServiceDefaults;

/// <summary>
/// Latency targets of spec §6 (RNF-01), kept in configuration (section <c>Observability:Targets</c>) rather than in code,
/// so that the single value the thesis settles on (D7: UC-01 says &lt; 2 s, RNF-01 says &lt; 1 s) is changed in one place.
/// They are exported as the gauge <c>smartparking.target</c> (Prometheus <c>smartparking_target_seconds</c>, tag
/// <c>objective</c>) so that dashboards and the Phase 9 scripts compare the measured p95 with the same value.
/// A target left empty is not exported.
/// </summary>
public sealed class LatencyTargetOptions
{
    public const string SectionName = "Observability:Targets";

    /// <summary>UC-01 end to end, sensor → Redis, p95 (D7 default: 1 s).</summary>
    public TimeSpan? IngestionEndToEndP95 { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>UC-03 availability query, p95 (spec §6: 200 ms).</summary>
    public TimeSpan? AvailabilityQueryP95 { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>UC-02 transactional write, p95 (spec §6: 2 s).</summary>
    public TimeSpan? SessionWriteP95 { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Consistency window (outbox → read model), p95. The specification sets no target: empty by default.
    /// TODO(Diogo): value for the thesis (Cap. 4.5), if any.
    /// </summary>
    public TimeSpan? ConsistencyWindowP95 { get; set; }
}

/// <summary>Creates the <c>smartparking.target</c> gauge when the host starts (ADR-0009).</summary>
internal sealed class LatencyTargetsGauge : IHostedService
{
    public const string InstrumentName = "smartparking.target";

    public LatencyTargetsGauge(IMeterFactory meterFactory, IOptionsMonitor<LatencyTargetOptions> options)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        ArgumentNullException.ThrowIfNull(options);
        meterFactory.Create(SmartParkingTelemetry.MeterName).CreateObservableGauge(
            InstrumentName,
            () => Measure(options.CurrentValue),
            "s",
            "Latency targets (p95) of spec §6, from configuration.");
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static IEnumerable<Measurement<double>> Measure(LatencyTargetOptions targets)
    {
        foreach (var (objective, value) in new[]
        {
            ("uc01_ingestion_e2e_p95", targets.IngestionEndToEndP95),
            ("uc03_availability_query_p95", targets.AvailabilityQueryP95),
            ("uc02_session_write_p95", targets.SessionWriteP95),
            ("consistency_window_p95", targets.ConsistencyWindowP95),
        })
        {
            if (value is { } target)
            {
                yield return new Measurement<double>(target.TotalSeconds, new KeyValuePair<string, object?>("objective", objective));
            }
        }
    }
}
