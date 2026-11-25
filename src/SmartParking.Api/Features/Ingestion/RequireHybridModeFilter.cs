using Microsoft.Extensions.Options;
using SmartParking.Infrastructure;

namespace SmartParking.Api.Features.Ingestion;

/// <summary>
/// The ingestion only has a hybrid event log until F9 (spec 003, ronda 5). In Monolithic mode the endpoint answers 501
/// with ProblemDetails before validation or any handler runs, so nothing is read or written (AC-16).
/// </summary>
internal sealed class RequireHybridModeFilter(IOptions<PersistenceOptions> options) : IEndpointFilter
{
    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(next);

        return PersistenceOptions.TryParseMode(options.Value.Mode, out var mode) && mode == PersistenceMode.Hybrid
            ? next(context)
            : ValueTask.FromResult<object?>(TypedResults.Problem(
                detail: "Occupancy ingestion in Monolithic mode arrives in F9.",
                statusCode: StatusCodes.Status501NotImplemented,
                title: "Ingestion.NotImplementedInMode"));
    }
}
