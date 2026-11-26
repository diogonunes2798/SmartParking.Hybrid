namespace SmartParking.SensorSimulator.Wire;

/// <summary>Body of <c>POST /api/ingest/occupancy-events</c>: 1 to 500 events (spec 003).</summary>
internal sealed record OccupancyBatchRequest(IReadOnlyList<OccupancyEventRequest> Events)
{
    /// <summary>The Api's limit per request (spec 003).</summary>
    public const int MaxEvents = 500;
}
