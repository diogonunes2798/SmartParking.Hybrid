using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using SmartParking.Api.Common;
using SmartParking.Application.Abstractions;
using SmartParking.Infrastructure.Hosting;

namespace SmartParking.Api.Tests.Fixtures;

/// <summary>
/// Test-only endpoints for the cross-cutting behaviour of spec 000
/// (unhandled exception, Result to ProblemDetails, validation filter).
/// </summary>
internal sealed class TestEndpoints : IEndpointGroup
{
    public const string Throw = "/_test/throw";

    public const string Errors = "/_test/errors";

    public const string Validated = "/_test/validated";

    /// <summary>Must never reach the client outside Development (AC-10).</summary>
    public const string SecretMessage = "sensitive-internal-detail";

    public static void Register(IServiceCollection services)
    {
        services.AddSingleton<IEndpointGroup, TestEndpoints>();
        services.AddSingleton<ValidationProbe>();
        services.AddScoped<IValidator<TestRequest>, TestRequestValidator>();
    }

    public void Map(IEndpointRouteBuilder app)
    {
        app.MapGet(Throw, ThrowUnhandled).AllowAnonymous();

        app.MapGet(Errors + "/{type}", (ErrorType type) => ToError(type).ToProblem()).AllowAnonymous();

        app.MapPost(Validated, (TestRequest request, ValidationProbe probe) =>
            {
                probe.RecordCall();
                return TypedResults.Ok(request);
            })
            .WithRequestValidation<TestRequest>()
            .AllowAnonymous();
    }

    private static Ok ThrowUnhandled() => throw new InvalidOperationException(SecretMessage);

    private static Error ToError(ErrorType type) => type switch
    {
        ErrorType.NotFound => Error.NotFound("Test.NotFound", "Not found."),
        ErrorType.Conflict => Error.Conflict("Test.Conflict", "Conflict."),
        ErrorType.Unauthorized => Error.Unauthorized("Test.Unauthorized", "Unauthorized."),
        ErrorType.PreconditionFailed => Error.PreconditionFailed("Test.PreconditionFailed", "Stale version."),
        ErrorType.PreconditionRequired => Error.PreconditionRequired("Test.PreconditionRequired", "If-Match is required."),
        ErrorType.Validation => Error.Validation(
            "Test.Validation",
            "Invalid.",
            new Dictionary<string, string[]> { ["name"] = ["Name is required."] }),
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };
}
