using System.Diagnostics.Metrics;

namespace SmartParking.Application.Features.Ingestion;

/// <summary>
/// Instruments of the <c>SmartParking.Ingestion</c> meter (spec 003, «Non-Functional Requirements»): events by
/// outcome (with the code of the rejected ones), batch size, and the duration of the registry query and of the append.
/// Exported over OTLP like the rest (F1); the CSV export belongs to F12.
/// </summary>
public sealed class IngestionMetrics
{
    public const string MeterName = "SmartParking.Ingestion";

    public const string OutcomeTag = "outcome";
    public const string CodeTag = "code";

    public const string Accepted = "accepted";
    public const string Duplicate = "duplicate";
    public const string Rejected = "rejected";

    private readonly Counter<long> events;
    private readonly Histogram<int> batchSize;
    private readonly Histogram<double> validationDuration;
    private readonly Histogram<double> appendDuration;

    public IngestionMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);

        var meter = meterFactory.Create(MeterName);
        events = meter.CreateCounter<long>("ingestion.events", "{event}", "Occupancy events by outcome.");
        batchSize = meter.CreateHistogram<int>("ingestion.batch.size", "{event}", "Events per ingestion request.");
        validationDuration = meter.CreateHistogram<double>(
            "ingestion.validation.duration", "s", "Duration of the registry query of one batch.");
        appendDuration = meter.CreateHistogram<double>(
            "ingestion.append.duration", "s", "Duration of the append of one batch to the event log.");
    }

    public void BatchReceived(int size) => batchSize.Record(size);

    public void EventsAccepted(int count) => AddIfAny(count, new KeyValuePair<string, object?>(OutcomeTag, Accepted));

    public void EventsDuplicated(int count) => AddIfAny(count, new KeyValuePair<string, object?>(OutcomeTag, Duplicate));

    public void EventRejected(string code) =>
        events.Add(1, new KeyValuePair<string, object?>(OutcomeTag, Rejected), new KeyValuePair<string, object?>(CodeTag, code));

    public void ValidationCompleted(TimeSpan elapsed) => validationDuration.Record(elapsed.TotalSeconds);

    public void AppendCompleted(TimeSpan elapsed) => appendDuration.Record(elapsed.TotalSeconds);

    private void AddIfAny(int count, KeyValuePair<string, object?> outcome)
    {
        if (count > 0)
        {
            events.Add(count, outcome);
        }
    }
}
