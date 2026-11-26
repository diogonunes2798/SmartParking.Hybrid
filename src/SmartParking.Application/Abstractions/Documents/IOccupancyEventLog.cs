using SmartParking.Domain.Occupancy;

namespace SmartParking.Application.Abstractions.Documents;

/// <summary>
/// Append-only occupancy event log (D3, spec 003). Appending is the only operation: events are never changed or
/// deleted (AC-15). Each subsystem and <c>Persistence:Mode</c> has its own implementation; F4 only has MongoDB.
/// The call returns only after the events are durably written. Events whose <c>eventId</c> is already in the log are
/// left untouched and reported as duplicates. Any other failure is an <see cref="ErrorType.Unavailable"/> error;
/// some events may have been written, and repeating the call is safe.
/// </summary>
public interface IOccupancyEventLog
{
    Task<Result<AppendOutcome>> AppendAsync(IReadOnlyList<OccupancyEvent> events, CancellationToken cancellationToken);
}
