using SmartParking.Application.Analytics;

namespace SmartParking.Workers.Analytics;

/// <summary>
/// Hosts the occupancy aggregation (UC-04, RF-05, D4 default, ADR-0010): every <see cref="AnalyticsOptions.Interval"/>,
/// aggregates the hours closed since the last run from the MongoDB log into <c>analytics.occupancy_hourly</c>. While a
/// backlog remains (a run hit <see cref="AnalyticsOptions.MaxHoursPerRun"/>), the next run starts at once. Several Workers
/// instances may run it: the upsert makes a window aggregated twice give the same rows. Failures are logged and retried.
/// </summary>
internal sealed partial class OccupancyAggregationService(
    IServiceScopeFactory scopes,
    AnalyticsOptions options,
    AnalyticsMetrics metrics,
    TimeProvider time,
    ILogger<OccupancyAggregationService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            var wait = options.Interval;
            try
            {
                var scope = scopes.CreateAsyncScope();
                await using (scope.ConfigureAwait(false))
                {
                    var report = await scope.ServiceProvider.GetRequiredService<OccupancyAggregator>()
                        .RunOnceAsync(stoppingToken).ConfigureAwait(false);
                    if (report.Hours > 0)
                    {
                        LogAggregated(report.From, report.To, report.Facilities, report.Rows, report.Events, report.Duration.TotalMilliseconds);
                    }

                    if (report.Hours >= options.MaxHoursPerRun)
                    {
                        wait = TimeSpan.Zero; // backlog: keep catching up
                    }
                }

                failures = 0;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Nothing is half-written: the rows and the checkpoint are saved in one transaction.
                failures++;
                metrics.Failed();
                LogFailed(ex, failures);
                wait = options.RetryDelay;
            }

            if (wait > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(wait, time, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Occupancy aggregated from {From:O} to {To:O}: {Facilities} facilities, {Rows} rows, {Events} events in {ElapsedMs:F0} ms.")]
    private partial void LogAggregated(DateTimeOffset? from, DateTimeOffset? to, int facilities, int rows, int events, double elapsedMs);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Occupancy aggregation failed ({Failures} in a row); retrying.")]
    private partial void LogFailed(Exception exception, int failures);
}
