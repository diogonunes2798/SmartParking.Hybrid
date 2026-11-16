using System.Diagnostics.Metrics;
using SmartParking.Application.Diagnostics;

namespace SmartParking.Application.Availability;

/// <summary>
/// Metrics of the availability read model (meter <c>SmartParking</c>, ADR-0002):
/// <list type="bullet">
/// <item><c>smartparking.read_model.events</c>: occupancy events handled by the projector, by <c>outcome</c>
/// (<c>applied</c>, <c>unchanged</c>, <c>duplicate</c>, <c>stale</c>, <c>unknown_spot</c>, <c>poison</c>);</item>
/// <item><c>smartparking.ingestion.e2e_latency</c>: seconds from the sensor timestamp to the Redis update. This is the
/// end-to-end latency of UC-01 (D7, RNF-01: &lt; 1 s p95). It includes the sensor's clock offset. Called
/// <c>smartparking.read_model.lag</c> until Phase 5 (renamed so that the name says what it measures, ADR-0009);</item>
/// <item><c>smartparking.read_model.bus_lag</c>: seconds from API reception to the Redis update (no sensor clock);</item>
/// <item><c>smartparking.read_model.configuration</c>: configuration events (RF-06), by <c>type</c> and <c>outcome</c>;</item>
/// <item><c>smartparking.consistency.window</c>: seconds from the outbox <c>occurred_at</c> (API clock) to the read model
/// update (Workers clock), by <c>type</c>: the eventual consistency window of Cap. 4.5 (UC-02/RF-06, ADR-0009);</item>
/// <item><c>smartparking.read_model.rebuild.duration</c>: seconds of a full rebuild (ADR-0008; used in Phase 9).</item>
/// </list>
/// Lag is recorded only for events that updated the spot now (applied or unchanged): a duplicate or stale
/// redelivery after a recovery would add minutes for an update that happened earlier.
/// </summary>
public sealed class ReadModelMetrics
{
    /// <summary>UC-01 end-to-end latency (D7). Prometheus: <c>smartparking_ingestion_e2e_latency_seconds</c>.</summary>
    public const string EndToEndLatencyInstrument = "smartparking.ingestion.e2e_latency";

    /// <summary>Consistency window (Cap. 4.5). Prometheus: <c>smartparking_consistency_window_seconds</c>.</summary>
    public const string ConsistencyWindowInstrument = "smartparking.consistency.window";

    private readonly Counter<long> events;
    private readonly Counter<long> configuration;
    private readonly Histogram<double> lag;
    private readonly Histogram<double> busLag;
    private readonly Histogram<double> rebuild;
    private readonly Histogram<double> consistencyWindow;

    public ReadModelMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        var meter = meterFactory.Create(SmartParkingTelemetry.MeterName);
        events = meter.CreateCounter<long>("smartparking.read_model.events", "{event}", "Occupancy events handled by the read model projector.");
        configuration = meter.CreateCounter<long>("smartparking.read_model.configuration", "{event}", "Configuration events applied to the read model.");
        lag = meter.CreateHistogram<double>(EndToEndLatencyInstrument, "s", "UC-01 end to end: sensor timestamp to the read model update (D7).");
        busLag = meter.CreateHistogram<double>("smartparking.read_model.bus_lag", "s", "Time from API reception to the read model update.");
        rebuild = meter.CreateHistogram<double>("smartparking.read_model.rebuild.duration", "s", "Duration of a full read model rebuild.");
        consistencyWindow = meter.CreateHistogram<double>(
            ConsistencyWindowInstrument, "s", "Eventual consistency window: outbox occurred_at to the read model update.");
    }

    public void Handled(ApplyOutcome outcome) => events.Add(1, new KeyValuePair<string, object?>("outcome", Tag(outcome)));

    public void Poison(int count)
    {
        if (count > 0)
        {
            events.Add(count, new KeyValuePair<string, object?>("outcome", "poison"));
        }
    }

    public void Lag(TimeSpan sinceSensor, TimeSpan sinceReception)
    {
        lag.Record(sinceSensor.TotalSeconds);
        busLag.Record(sinceReception.TotalSeconds);
    }

    public void Configuration(string type, string outcome) =>
        configuration.Add(1, new KeyValuePair<string, object?>("type", type), new KeyValuePair<string, object?>("outcome", outcome));

    public void ConsistencyWindow(string type, TimeSpan value) =>
        consistencyWindow.Record(value.TotalSeconds, new KeyValuePair<string, object?>("type", type));

    public void Rebuilt(TimeSpan duration) => rebuild.Record(duration.TotalSeconds);

    private static string Tag(ApplyOutcome outcome) => outcome switch
    {
        ApplyOutcome.Applied => "applied",
        ApplyOutcome.Unchanged => "unchanged",
        ApplyOutcome.Duplicate => "duplicate",
        ApplyOutcome.Stale => "stale",
        ApplyOutcome.UnknownSpot => "unknown_spot",
        _ => "other",
    };
}
