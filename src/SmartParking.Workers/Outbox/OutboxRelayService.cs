using Microsoft.Extensions.Options;
using SmartParking.Application.Abstractions;
using SmartParking.Application.Outbox;

namespace SmartParking.Workers.Outbox;

/// <summary>
/// Hosts the outbox relay (UC-02, RF-06; spec §4; ADR-0009). Relays batches back to back while they come full, then
/// waits <see cref="OutboxRelayOptions.PollInterval"/>. Any number of Workers instances may run it: the claim uses
/// <c>FOR UPDATE SKIP LOCKED</c>, so instances share the backlog without publishing a row twice (except after a crash
/// between the publish and the commit: at least once). Failures (database or bus down) are logged and retried.
/// </summary>
internal sealed partial class OutboxRelayService(
    IServiceScopeFactory scopes,
    IOptions<OutboxRelayOptions> options,
    OutboxMetrics metrics,
    TimeProvider time,
    ILogger<OutboxRelayService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        var nextPendingRefresh = DateTimeOffset.MinValue;
        var failures = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            var wait = settings.PollInterval;
            try
            {
                var scope = scopes.CreateAsyncScope();
                await using (scope.ConfigureAwait(false))
                {
                    var relay = scope.ServiceProvider.GetRequiredService<OutboxRelay>();
                    var published = await relay.RelayBatchAsync(settings.BatchSize, stoppingToken).ConfigureAwait(false);
                    if (published == settings.BatchSize)
                    {
                        wait = TimeSpan.Zero; // full batch: there is probably more
                    }

                }

                if (failures > 0)
                {
                    LogRecovered(failures);
                    failures = 0;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Rolled back: the rows stay unprocessed and are claimed again.
                failures++;
                metrics.Failed();
                LogFailed(ex, failures);
                wait = settings.RetryDelay;
            }

            if (time.GetUtcNow() >= nextPendingRefresh && !stoppingToken.IsCancellationRequested)
            {
                await RefreshPendingAsync(stoppingToken).ConfigureAwait(false);
                nextPendingRefresh = time.GetUtcNow() + settings.PendingRefreshInterval;
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

    /// <summary>
    /// Backlog gauge, apart from the batch (review of Phase 5, finding 6): a failed count is not a failed batch, and while
    /// PostgreSQL is down the gauge disappears instead of showing its last value.
    /// </summary>
    private async Task RefreshPendingAsync(CancellationToken stoppingToken)
    {
        try
        {
            var scope = scopes.CreateAsyncScope();
            await using (scope.ConfigureAwait(false))
            {
                var store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
                metrics.Pending(await store.CountPendingAsync(stoppingToken).ConfigureAwait(false));
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            metrics.Pending(-1);
            LogPendingFailed(ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not count the pending outbox rows.")]
    private partial void LogPendingFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Outbox relay batch failed ({Failures} in a row); retrying.")]
    private partial void LogFailed(Exception exception, int failures);

    [LoggerMessage(Level = LogLevel.Information, Message = "Outbox relay recovered after {Failures} failed batches.")]
    private partial void LogRecovered(int failures);
}
