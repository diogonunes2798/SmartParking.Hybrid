using SmartParking.Application.Abstractions;
using SmartParking.Application.Abstractions.Documents;
using SmartParking.Domain.Occupancy;

namespace SmartParking.Api.Tests.Fixtures;

/// <summary>An event log that is always unreachable, as the MongoDB log reports it (spec 003, 503 path). Writes nothing.</summary>
internal sealed class UnavailableEventLog : IOccupancyEventLog
{
    public Task<Result<AppendOutcome>> AppendAsync(IReadOnlyList<OccupancyEvent> events, CancellationToken cancellationToken) =>
        Task.FromResult<Result<AppendOutcome>>(Error.Unavailable("Ingestion.EventLogUnavailable", "Simulated outage."));
}
