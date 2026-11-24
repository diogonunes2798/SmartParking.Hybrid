namespace SmartParking.Application.Abstractions.Paging;

/// <summary>Page of a list query (spec 001): <c>page</c> ≥ 1 and <c>pageSize</c> between 1 and 100, 20 by default.</summary>
public sealed record PageRequest(int Page = PageRequest.FirstPage, int PageSize = PageRequest.DefaultPageSize)
{
    public const int FirstPage = 1;

    public const int DefaultPageSize = 20;

    public const int MaxPageSize = 100;

    /// <summary>Rows to skip before this page. Only meaningful once the request has been validated.</summary>
    public int Offset => (Page - 1) * PageSize;
}
