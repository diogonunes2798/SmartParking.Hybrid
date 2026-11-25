using Microsoft.AspNetCore.Http.HttpResults;
using SmartParking.Application.Abstractions;

namespace SmartParking.Api.Common;

/// <summary>Maps a failed <see cref="Result"/> to ProblemDetails (RFC 9457) at the API boundary.</summary>
internal static class ResultExtensions
{
    public static IResult ToProblem(this Result result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return result.Error is { } error
            ? error.ToProblem()
            : throw new InvalidOperationException("A successful result cannot be converted to a problem.");
    }

    public static IResult ToProblem(this Error error)
    {
        ArgumentNullException.ThrowIfNull(error);

        return error.Type switch
        {
            ErrorType.Validation => TypedResults.ValidationProblem(
                error.ValidationErrors.ToDictionary(),
                detail: error.Description,
                title: error.Code),
            ErrorType.NotFound => Problem(error, StatusCodes.Status404NotFound),
            ErrorType.Conflict => Problem(error, StatusCodes.Status409Conflict),
            ErrorType.Unauthorized => Problem(error, StatusCodes.Status401Unauthorized),
            ErrorType.PreconditionFailed => Problem(error, StatusCodes.Status412PreconditionFailed),
            ErrorType.PreconditionRequired => Problem(error, StatusCodes.Status428PreconditionRequired),
            ErrorType.PaymentDeclined => Problem(error, StatusCodes.Status402PaymentRequired),

            // The endpoint sets Retry-After, since only it knows the configured delay (spec 003).
            ErrorType.Unavailable => Problem(error, StatusCodes.Status503ServiceUnavailable),
            _ => throw new ArgumentOutOfRangeException(nameof(error), error.Type, "Unknown error type."),
        };
    }

    /// <summary>The error's extensions (for example the <c>paymentId</c> of a declined payment) become ProblemDetails members.</summary>
    private static ProblemHttpResult Problem(Error error, int statusCode) =>
        TypedResults.Problem(
            detail: error.Description,
            statusCode: statusCode,
            title: error.Code,
            extensions: error.Extensions.Count == 0 ? null : error.Extensions);
}
