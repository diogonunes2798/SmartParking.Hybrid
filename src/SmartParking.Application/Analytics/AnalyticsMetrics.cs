using System.Diagnostics.Metrics;
using SmartParking.Application.Diagnostics;

namespace SmartParking.Application.Analytics;

/// <summary>
/// Metrics of the occupancy aggregation (meter <c>SmartParking</c>, ADR-0002, ADR-0010):
/// <list type="bullet">
/// <item><c>smartparking.analytics.runs</c>: aggregations, by <c>outcome</c> (<c>aggregated</c>, <c>failed</c>);</item>
/// <item><c>smartparking.analytics.rows</c>: summary rows upserted;</item>
/// <item><c>smartparking.analytics.duration</c>: seconds per aggregation (cost of the analytical layer, sections 6.2 and 6.5);</item>
/// <item><c>smartparking.analytics.staleness</c>: seconds from the end of the last aggregated hour to now, i.e. how old the
/// newest data the report can show is (freshness of the summary tables).</item>
/// </list>
/// </summary>
public sealed class AnalyticsMetrics
{
    private readonly Counter<long> runs;
    private readonly Counter<long> rows;
    private readonly Histogram<double> duration;
    private readonly TimeProvider time;
    private long checkpointTicks = -1;

    public AnalyticsMetrics(IMeterFactory meterFactory, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        this.time = time ?? throw new ArgumentNullException(nameof(time));
        var meter = meterFactory.Create(SmartParkingTelemetry.MeterName);
        runs = meter.CreateCounter<long>("smartparking.analytics.runs", "{run}", "Occupancy aggregations, by outcome.");
        rows = meter.CreateCounter<long>("smartparking.analytics.rows", "{row}", "Hourly summary rows upserted.");
        duration = meter.CreateHistogram<double>("smartparking.analytics.duration", "s", "Duration of one occupancy aggregation.");
        meter.CreateObservableGauge(
            "smartparking.analytics.staleness",
            () => Interlocked.Read(ref checkpointTicks) is var ticks and >= 0
                ? [new Measurement<double>((this.time.GetUtcNow() - new DateTimeOffset(ticks, TimeSpan.Zero)).TotalSeconds)]
                : Array.Empty<Measurement<double>>(),
            "s",
            "Time from the end of the last aggregated hour to now.");
    }

    public void Aggregated(AggregationReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        runs.Add(1, new KeyValuePair<string, object?>("outcome", "aggregated"));
        rows.Add(report.Rows);
        duration.Record(report.Duration.TotalSeconds);
    }

    public void Failed() => runs.Add(1, new KeyValuePair<string, object?>("outcome", "failed"));

    public void Checkpoint(DateTimeOffset? checkpoint)
    {
        if (checkpoint is { } value)
        {
            Interlocked.Exchange(ref checkpointTicks, value.UtcTicks);
        }
    }
}
