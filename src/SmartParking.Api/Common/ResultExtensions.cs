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
            _ => throw new ArgumentOutOfRangeException(nameof(error), error.Type, "Unknown error type."),
        };
    }

    private static ProblemHttpResult Problem(Error error, int statusCode) =>
        TypedResults.Problem(detail: error.Description, statusCode: statusCode, title: error.Code);
}
