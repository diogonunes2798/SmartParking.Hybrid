namespace SmartParking.Api.Features.Spots;

/// <summary>Response of a batch (201): the created ids, in the order of the request (plan 001b, point 7).</summary>
internal sealed record SpotBatchResponse(IReadOnlyList<SpotBatchItemResponse> Items);
