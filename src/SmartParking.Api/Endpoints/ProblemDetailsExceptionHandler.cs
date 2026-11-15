using Microsoft.AspNetCore.Diagnostics;
using SmartParking.Application.Common;
using SmartParking.Domain.Common;

namespace SmartParking.Api.Endpoints;

/// <summary>
/// Maps domain and application exceptions to RFC 9457 ProblemDetails responses:
/// <list type="bullet">
/// <item>invalid input (domain invariant or unreadable body) → 400, with the offending field in <c>errors</c>;</item>
/// <item>missing resource → 404;</item>
/// <item>concurrency, uniqueness or reference conflict → 409.</item>
/// </list>
/// Anything else falls through to the default 500 handling.
/// </summary>
internal sealed class ProblemDetailsExceptionHandler(IProblemDetailsService problemDetails) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (status, title, errors) = exception switch
        {
            DomainValidationException v => (
                StatusCodes.Status400BadRequest,
                "Invalid request.",
                new Dictionary<string, string[]>(StringComparer.Ordinal) { [v.Field] = [v.Reason] }),
            BadHttpRequestException => (StatusCodes.Status400BadRequest, "The request body could not be read.", null),
            NotFoundException => (StatusCodes.Status404NotFound, "Resource not found.", null),
            ConcurrencyConflictException => (StatusCodes.Status409Conflict, "Concurrency conflict.", null),
            UniqueConstraintException => (StatusCodes.Status409Conflict, "Duplicate value.", null),
            ReferentialIntegrityException => (StatusCodes.Status409Conflict, "Referential integrity conflict.", null),
            _ => (0, string.Empty, (Dictionary<string, string[]>?)null),
        };

        if (status == 0)
        {
            return false;
        }

        httpContext.Response.StatusCode = status;
        var context = new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails =
            {
                Status = status,
                Title = title,
                Detail = exception is BadHttpRequestException ? exception.InnerException?.Message : exception.Message,
            },
        };

        if (errors is not null)
        {
            context.ProblemDetails.Extensions["errors"] = errors;
        }

        return await problemDetails.TryWriteAsync(context);
    }
}
