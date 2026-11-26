using SmartParking.Application.Abstractions;
using SmartParking.Application.Abstractions.Documents;
using SmartParking.Domain.Occupancy;

namespace SmartParking.Infrastructure;

/// <summary>
/// Placeholder for the monolithic event log, which comes in F9 (spec 003, ronda 5). It is never called: the ingestion
/// endpoint answers 501 in Monolithic mode before any handler runs. It exists only so the handler can be built in both
/// modes. If it were reached, that would be a programming error.
/// </summary>
internal sealed class MonolithicOccupancyEventLogPending : IOccupancyEventLog
{
    public Task<Result<AppendOutcome>> AppendAsync(IReadOnlyList<OccupancyEvent> events, CancellationToken cancellationToken) =>
        throw new NotSupportedException("The monolithic occupancy event log arrives in F9.");
}
