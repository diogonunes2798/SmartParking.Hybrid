using Microsoft.Extensions.Options;
using SmartParking.Infrastructure;

namespace SmartParking.Api.Common;

/// <summary>
/// Endpoints whose monolithic implementation arrives in F9 (ingestion, spec 003; availability, spec 004). In Monolithic
/// mode the endpoint answers 501 with ProblemDetails before validation or any handler runs, so nothing is read or
/// written. Add it before <see cref="ValidationFilterExtensions.WithRequestValidation{TRequest}"/>.
/// </summary>
internal static class HybridModeFilterExtensions
{
    public static RouteHandlerBuilder RequireHybridMode(this RouteHandlerBuilder builder, string title, string detail) =>
        builder
            .AddEndpointFilter((context, next) =>
            {
                var options = context.HttpContext.RequestServices.GetRequiredService<IOptions<PersistenceOptions>>();
                return PersistenceOptions.TryParseMode(options.Value.Mode, out var mode) && mode == PersistenceMode.Hybrid
                    ? next(context)
                    : ValueTask.FromResult<object?>(TypedResults.Problem(
                        detail: detail,
                        statusCode: StatusCodes.Status501NotImplemented,
                        title: title));
            })
            .ProducesProblem(StatusCodes.Status501NotImplemented);
}
