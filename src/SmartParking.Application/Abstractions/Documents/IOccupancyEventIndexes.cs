namespace SmartParking.Application.Abstractions.Documents;

/// <summary>Creates the indexes of the event log that reads need (spec 004, «Índice no MongoDB»). Idempotent.</summary>
public interface IOccupancyEventIndexes
{
    Task EnsureAsync(CancellationToken cancellationToken);
}
