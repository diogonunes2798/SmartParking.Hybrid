namespace SmartParking.Application.Abstractions.Paging;

/// <summary>One page of a list query, with the total number of matching items (spec 001).</summary>
public sealed record PagedResult<TItem>(IReadOnlyList<TItem> Items, int Page, int PageSize, int TotalCount);
