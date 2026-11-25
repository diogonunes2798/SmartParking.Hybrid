namespace SmartParking.Api.Common;

/// <summary>List responses (spec 001): <c>{ items, page, pageSize, totalCount }</c>.</summary>
internal sealed record PagedResponse<TItem>(IReadOnlyList<TItem> Items, int Page, int PageSize, int TotalCount);
