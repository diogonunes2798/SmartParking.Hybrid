using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using SmartParking.Application.Abstractions.Documents;

namespace SmartParking.Infrastructure.Documents;

/// <summary>
/// Creates the event log indexes when a hybrid host starts (spec 004, ronda 2). It runs in the background and never
/// stops the host: an unreachable MongoDB is reported by the readiness check, and the next start (or the
/// <c>ProjectionRebuilder</c>, which creates the index itself) tries again (ADR 0010).
/// </summary>
internal sealed partial class DocumentsInitializer(IOccupancyEventIndexes indexes, ILogger<DocumentsInitializer> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await indexes.EnsureAsync(stoppingToken);
        }
        catch (MongoException exception)
        {
            LogIndexFailed(exception);
        }
        catch (TimeoutException exception)
        {
            LogIndexFailed(exception);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The host stopped before MongoDB answered; nothing to report.
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "The occupancy event indexes could not be created at start")]
    private partial void LogIndexFailed(Exception exception);
}
