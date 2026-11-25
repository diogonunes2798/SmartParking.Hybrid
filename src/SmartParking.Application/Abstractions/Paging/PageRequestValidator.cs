using FluentValidation;

namespace SmartParking.Application.Abstractions.Paging;

/// <summary>
/// Shared paging rules; list query validators reuse it with <c>SetValidator</c>.
/// Errors are keyed by the query string names (<c>page</c>, <c>pageSize</c>) in the ValidationProblemDetails.
/// </summary>
public sealed class PageRequestValidator : AbstractValidator<PageRequest>
{
    public PageRequestValidator()
    {
        RuleFor(request => request.Page)
            .GreaterThanOrEqualTo(PageRequest.FirstPage)
            .OverridePropertyName("page");

        RuleFor(request => request.PageSize)
            .InclusiveBetween(1, PageRequest.MaxPageSize)
            .OverridePropertyName("pageSize");
    }
}
