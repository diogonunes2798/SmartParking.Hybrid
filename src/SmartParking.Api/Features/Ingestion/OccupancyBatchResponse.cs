namespace SmartParking.Api.Features.Ingestion;

/// <summary>
/// Response of a batch (200, spec 003): the counts, and in <see cref="Issues"/> only the events not accepted, in
/// request order. A batch where every event was rejected is still a 200.
/// </summary>
internal sealed record OccupancyBatchResponse(int Accepted, int Duplicates, int Rejected, IReadOnlyList<OccupancyIssueResponse> Issues);
