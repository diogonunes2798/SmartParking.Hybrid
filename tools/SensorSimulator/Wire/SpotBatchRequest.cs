namespace SmartParking.SensorSimulator.Wire;

/// <summary>Body of <c>POST /api/facilities/{facilityId}/spots/batch</c>: 1 to 1 000 spots, all or nothing (spec 001).</summary>
internal sealed record SpotBatchRequest(IReadOnlyList<SpotBatchItemRequest> Spots)
{
    /// <summary>The Api's limit per request (spec 001, AC-15).</summary>
    public const int MaxItems = 1_000;
}
