using SmartParking.Application.Retention;

namespace SmartParking.Workers.Retention;

/// <summary>
/// Runs the retention of the raw events every <see cref="RetentionOptions.Interval"/> (RNF-05, Phase 10, ADR-0013), in
/// both variants. A failed run (a database down) is logged and retried on the next interval; the MongoDB TTL index keeps
/// the last value set by the guard meanwhile (<see cref="RetentionOptions.GuardMargin"/>).
/// </summary>
internal sealed partial class RetentionService(
    IServiceScopeFactory scopes,
    RetentionOptions options,
    TimeProvider time,
    ILogger<RetentionService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var scope = scopes.CreateAsyncScope();
                await using (scope.ConfigureAwait(false))
                {
                    var report = await scope.ServiceProvider.GetRequiredService<OccupancyRetention>().RunOnceAsync(stoppingToken)
                        .ConfigureAwait(false);
                    if (report.DaysArchived > 0 || report.Enforcement?.DroppedPartitions.Count > 0)
                    {
                        LogRun(report.DaysArchived, report.EventsArchived, report.ArchivedUntil, report.PurgeHorizon,
                            report.Enforcement?.ExpireAfterSeconds, report.Enforcement?.DroppedPartitions.Count ?? 0);
                    }

                    if (report.Blocked is { } blocked)
                    {
                        LogBlocked(blocked);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                LogFailed(ex);
            }

            try
            {
                await Task.Delay(options.Interval, time, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Retention: {Days} days archived ({Events} events), archived until {ArchivedUntil}, purge horizon {Horizon}, TTL {ExpireAfterSeconds} s, {Dropped} partitions dropped.")]
    private partial void LogRun(int days, long events, DateTimeOffset? archivedUntil, DateTimeOffset? horizon, long? expireAfterSeconds, int dropped);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Retention is waiting: {Reason}.")]
    private partial void LogBlocked(string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Retention run failed; retried on the next interval.")]
    private partial void LogFailed(Exception exception);
}
