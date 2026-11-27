using System.Globalization;
using Microsoft.Extensions.Options;
using SmartParking.Api.Common;
using SmartParking.Application.Abstractions;
using SmartParking.Application.Abstractions.Messaging;
using SmartParking.Application.Features.Ingestion;
using SmartParking.Infrastructure;
using SmartParking.Infrastructure.Hosting;

namespace SmartParking.Api.Features.Ingestion;

/// <summary>
/// Occupancy event ingestion from facility gateways (UC-01, RF-01, spec 003). The answer comes only after the accepted
/// events are durably in the event log. Anonymous on purpose: gateway identity and RBAC belong to RNF-06 (F11).
/// </summary>
internal sealed partial class IngestionEndpoints : IEndpointGroup
{
    public void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/ingest")
            .WithTags("Ingestion")
            .AllowAnonymous();

        // The mode filter goes first, so in Monolithic mode even an invalid batch is a 501 (nothing is read).
        group.MapPost("/occupancy-events", IngestAsync)
            .RequireHybridMode("Ingestion.NotImplementedInMode", "Occupancy ingestion in Monolithic mode arrives in F9.")
            .WithRequestValidation<OccupancyBatchRequest>()
            .Produces<OccupancyBatchResponse>()
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
    }

    private static async Task<IResult> IngestAsync(
        OccupancyBatchRequest request,
        ICommandHandler<IngestOccupancyBatch.Command, IngestOccupancyBatch.Outcome> handler,
        IOptions<IngestionOptions> options,
        ILogger<IngestionEndpoints> logger,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(request.ToCommand(), cancellationToken);
        if (result.Error is { } error)
        {
            if (error.Type == ErrorType.Unavailable)
            {
                response.Headers.RetryAfter = options.Value.RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
            }

            return error.ToProblem();
        }

        var outcome = result.Value;
        LogBatch(logger, request.Events!.Count, outcome.Accepted, outcome.Duplicates, outcome.Rejected);
        if (logger.IsEnabled(LogLevel.Debug))
        {
            foreach (var issue in outcome.Issues.Where(issue => issue.Code is not null))
            {
                LogRejected(logger, request.Events[issue.Index].SensorId, issue.Code);
            }
        }

        return TypedResults.Ok(new OccupancyBatchResponse(
            outcome.Accepted,
            outcome.Duplicates,
            outcome.Rejected,
            [.. outcome.Issues.Select(issue => new OccupancyIssueResponse(issue.Index, issue.EventId, issue.Status, issue.Code))]));
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information,
        Message = "Occupancy batch of {Size} events: {Accepted} accepted, {Duplicates} duplicates, {Rejected} rejected")]
    private static partial void LogBatch(ILogger logger, int size, int accepted, int duplicates, int rejected);

    [LoggerMessage(EventId = 2, Level = LogLevel.Debug, Message = "Occupancy event from sensor {HardwareId} rejected: {Code}")]
    private static partial void LogRejected(ILogger logger, string? hardwareId, string? code);
}
