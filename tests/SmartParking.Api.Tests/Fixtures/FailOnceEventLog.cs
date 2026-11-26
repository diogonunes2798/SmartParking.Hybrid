using Microsoft.Extensions.DependencyInjection;
using SmartParking.Application.Abstractions;
using SmartParking.Application.Abstractions.Documents;
using SmartParking.Domain.Occupancy;
using SmartParking.Infrastructure.Documents;

namespace SmartParking.Api.Tests.Fixtures;

/// <summary>
/// The MongoDB event log, unreachable on the first append only (spec 003, AC-24): the first batch gets the 503 with
/// <c>Retry-After</c> and nothing is written; every later append goes to the real log.
/// </summary>
internal sealed class FailOnceEventLog(MongoOccupancyEventLog inner) : IOccupancyEventLog
{
    private int calls;

    public static void Register(IServiceCollection services)
    {
        services.AddSingleton<MongoOccupancyEventLog>();
        services.AddSingleton<IOccupancyEventLog, FailOnceEventLog>();
    }

    public Task<Result<AppendOutcome>> AppendAsync(IReadOnlyList<OccupancyEvent> events, CancellationToken cancellationToken) =>
        Interlocked.Increment(ref calls) == 1
            ? Task.FromResult<Result<AppendOutcome>>(Error.Unavailable("Ingestion.EventLogUnavailable", "Simulated outage on the first append."))
            : inner.AppendAsync(events, cancellationToken);
}
