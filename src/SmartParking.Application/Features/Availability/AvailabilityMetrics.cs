using System.Diagnostics.Metrics;
using SmartParking.Application.Abstractions.Availability;

namespace SmartParking.Application.Features.Availability;

/// <summary>
/// Instruments of the <c>SmartParking.Availability</c> meter (spec 004, «Non-Functional Requirements»): projector
/// events by outcome and the duration of a rebuild (RNF-03, measured in F12). Query latency comes from the ASP.NET Core
/// instrumentation (F1); the consistency window belongs to F6.
/// </summary>
public sealed class AvailabilityMetrics
{
    public const string MeterName = "SmartParking.Availability";

    public const string ProjectorEventsInstrument = "availability.projector.events";
    public const string RebuildDurationInstrument = "availability.rebuild.duration";

    public const string OutcomeTag = "outcome";

    public const string Applied = "applied";
    public const string Duplicate = "duplicate";
    public const string Stale = "stale";
    public const string UnknownSpot = "unknown_spot";

    private readonly Counter<long> projectorEvents;
    private readonly Histogram<double> rebuildDuration;

    public AvailabilityMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);

        var meter = meterFactory.Create(MeterName);
        projectorEvents = meter.CreateCounter<long>(
            ProjectorEventsInstrument, "{event}", "Occupancy events applied to the availability view, by outcome.");
        rebuildDuration = meter.CreateHistogram<double>(
            RebuildDurationInstrument, "s", "Duration of a rebuild of the availability view.");
    }

    public static string OutcomeName(ProjectionOutcome outcome) => outcome switch
    {
        ProjectionOutcome.Applied => Applied,
        ProjectionOutcome.Duplicate => Duplicate,
        ProjectionOutcome.Stale => Stale,
        ProjectionOutcome.UnknownSpot => UnknownSpot,
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null),
    };

    public void EventProjected(ProjectionOutcome outcome) =>
        projectorEvents.Add(1, new KeyValuePair<string, object?>(OutcomeTag, OutcomeName(outcome)));

    public void RebuildCompleted(TimeSpan elapsed) => rebuildDuration.Record(elapsed.TotalSeconds);
}
