using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using SmartParking.Application.Ingestion;

namespace SmartParking.Api.Endpoints;

/// <summary>
/// UC-01 ingestion (steps 1–2). The body is bound as a raw <see cref="JsonElement"/>, not as typed DTOs, so that one
/// malformed event is reported by index (RF-01) instead of making the whole request unreadable.
/// <list type="bullet">
/// <item>202: the valid events are on the bus (D2); <c>rejected</c> lists the invalid ones (partial acceptance,
/// <see cref="IngestionSettings.RejectWholeBatchOnInvalid"/>);</item>
/// <item>400: unreadable JSON, not an array, empty or oversized batch, or no valid event (<c>rejected</c> in the body);</item>
/// <item>503: the bus did not accept the events; the sensor should retry (idempotent by <c>eventId</c>).</item>
/// </list>
/// </summary>
internal static class IngestionEndpoints
{
    public static IEndpointRouteBuilder MapIngestionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/ingest/occupancy", async Task<Results<Accepted<IngestAccepted>, ProblemHttpResult>> (
                JsonElement body, IngestOccupancyBatchHandler handler, CancellationToken ct) =>
            {
                var outcome = await handler.HandleAsync(body, ct);
                if (outcome.Accepted is { } accepted)
                {
                    return TypedResults.Accepted((string?)null, accepted);
                }

                return TypedResults.Problem(
                    title: "Invalid occupancy batch.",
                    detail: outcome.Error,
                    statusCode: StatusCodes.Status400BadRequest,
                    extensions: new Dictionary<string, object?> { ["rejected"] = outcome.Rejected });
            })
            .WithTags("Ingestion")
            .Accepts<JsonElement>("application/json")
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        return app;
    }
}
