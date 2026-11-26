namespace SmartParking.Api.Features.Ingestion;

/// <summary><see cref="Status"/> is <c>duplicate</c> or <c>rejected</c>; <see cref="Code"/> is set only for rejected events.</summary>
internal sealed record OccupancyIssueResponse(int Index, Guid EventId, string Status, string? Code);
