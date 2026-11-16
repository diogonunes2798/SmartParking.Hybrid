using SmartParking.Infrastructure.Relational.Monolithic;

namespace SmartParking.Workers.Monolithic;

/// <summary>
/// Monolithic variant only (ADR-0012): creates the monthly partitions of <c>monolith.occupancy_events</c> ahead of time,
/// at start and then every <see cref="MonolithicOptions.PartitionMaintenanceInterval"/>. A failure (database down) is
/// logged and retried after one minute; the DEFAULT partition keeps the inserts working meanwhile.
/// </summary>
internal sealed partial class PartitionMaintenanceService(
    OccupancyPartitionMaintenance maintenance,
    MonolithicOptions options,
    TimeProvider time,
    ILogger<PartitionMaintenanceService> logger) : BackgroundService
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var wait = options.PartitionMaintenanceInterval;
            try
            {
                var created = await maintenance.EnsureAsync(stoppingToken).ConfigureAwait(false);
                LogEnsured(created, options.PartitionMonthsAhead);
                var inDefault = await maintenance.CountDefaultPartitionRowsAsync(stoppingToken).ConfigureAwait(false);
                if (inDefault > 0)
                {
                    LogDefaultRows(inDefault);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                LogFailed(ex);
                wait = RetryDelay;
            }

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

    [LoggerMessage(Level = LogLevel.Information, Message = "Occupancy partitions ensured: {Created} created ({MonthsAhead} months ahead).")]
    private partial void LogEnsured(int created, int monthsAhead);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The DEFAULT occupancy partition has {Rows} rows: events outside every monthly partition (ADR-0012).")]
    private partial void LogDefaultRows(long rows);

    [LoggerMessage(Level = LogLevel.Error, Message = "Occupancy partition maintenance failed; retrying in one minute.")]
    private partial void LogFailed(Exception exception);
}
