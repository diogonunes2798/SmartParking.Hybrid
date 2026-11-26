using MongoDB.Driver;
using SmartParking.Application.Abstractions;
using SmartParking.Application.Abstractions.Documents;
using SmartParking.Domain.Occupancy;

namespace SmartParking.Infrastructure.Documents;

/// <summary>
/// Append-only event log over MongoDB (D3, spec 003). It uses one unordered <c>insertMany</c> per batch and nothing
/// else: no update, replace or delete (AC-15). A duplicate key (E11000) on <c>_id</c> is an event already in the log.
/// Any other write error, a write concern error, a lost connection or a timeout fails the whole call as
/// <see cref="ErrorType.Unavailable"/>; the events written before the failure come back as duplicates on retry.
/// </summary>
internal sealed class MongoOccupancyEventLog(OccupancyEventsCollection events) : IOccupancyEventLog
{
    private static readonly InsertManyOptions Unordered = new() { IsOrdered = false };

    public async Task<Result<AppendOutcome>> AppendAsync(IReadOnlyList<OccupancyEvent> occupancyEvents, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(occupancyEvents);

        var documents = occupancyEvents.Select(OccupancyEventDocument.From).ToList();
        try
        {
            await events.Collection.InsertManyAsync(documents, Unordered, cancellationToken);
            return new AppendOutcome(new HashSet<Guid>());
        }
        catch (MongoBulkWriteException<OccupancyEventDocument> exception)
            when (exception.WriteConcernError is null
                && exception.WriteErrors.All(error => error.Category == ServerErrorCategory.DuplicateKey))
        {
            return new AppendOutcome(exception.WriteErrors.Select(error => documents[error.Index].EventId).ToHashSet());
        }
        catch (MongoException)
        {
            return Unavailable();
        }
        catch (TimeoutException)
        {
            return Unavailable();
        }
    }

    private static Error Unavailable() =>
        Error.Unavailable("Ingestion.EventLogUnavailable", "The occupancy event log could not be written. Retry later.");
}
