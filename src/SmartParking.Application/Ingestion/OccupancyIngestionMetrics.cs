using System.Diagnostics.Metrics;
using SmartParking.Application.Diagnostics;

namespace SmartParking.Application.Ingestion;

/// <summary>
/// Metrics of the UC-01 pipeline (exported through the <c>SmartParking</c> meter, ADR-0002):
/// <list type="bullet">
/// <item><c>smartparking.ingest.events</c>: events received by the API, by <c>outcome</c>
/// (<c>accepted</c>, <c>rejected</c>, <c>duplicate_in_batch</c>);</item>
/// <item><c>smartparking.occupancy_log.events</c>: events handled by the log writer, by <c>outcome</c>
/// (<c>inserted</c>, <c>duplicate</c>, <c>poison</c>). <c>duplicate</c> counts redeliveries and repeated requests;</item>
/// <item><c>smartparking.occupancy_log.lag</c>: seconds from API reception to the log write (bus segment of D7).</item>
/// </list>
/// </summary>
public sealed class OccupancyIngestionMetrics
{
    private readonly Counter<long> ingested;
    private readonly Counter<long> logged;
    private readonly Histogram<double> lag;

    public OccupancyIngestionMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        var meter = meterFactory.Create(SmartParkingTelemetry.MeterName);
        ingested = meter.CreateCounter<long>("smartparking.ingest.events", "{event}", "Occupancy events received by the API.");
        logged = meter.CreateCounter<long>("smartparking.occupancy_log.events", "{event}", "Occupancy events handled by the log writer.");
        lag = meter.CreateHistogram<double>("smartparking.occupancy_log.lag", "s", "Time from API reception to the event log write.");
    }

    public void Received(int accepted, int rejected, int duplicatesInBatch)
    {
        Add(ingested, accepted, "accepted");
        Add(ingested, rejected, "rejected");
        Add(ingested, duplicatesInBatch, "duplicate_in_batch");
    }

    public void Logged(int inserted, int duplicates, int poison)
    {
        Add(logged, inserted, "inserted");
        Add(logged, duplicates, "duplicate");
        Add(logged, poison, "poison");
    }

    public void LogLag(TimeSpan value) => lag.Record(value.TotalSeconds);

    private static void Add(Counter<long> counter, int value, string outcome)
    {
        if (value > 0)
        {
            counter.Add(value, new KeyValuePair<string, object?>("outcome", outcome));
        }
    }
}
