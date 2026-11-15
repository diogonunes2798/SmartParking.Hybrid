using SmartParking.Application.Availability;

namespace SmartParking.Workers.ReadModel;

/// <summary>
/// Initial load of the availability read model (ADR-0008). On start, if Redis has no loaded read model
/// (<c>sp:rm:meta</c> missing: first start, Redis restarted without persistence, or cleared), runs a full rebuild;
/// then opens the <see cref="ReadModelGate"/> so the projectors start consuming. Until then the projectors wait and
/// their messages stay unacknowledged in the streams. A failure (PostgreSQL, MongoDB or Redis down) is retried with
/// back-off; the projectors keep waiting meanwhile. With several Workers starting together, one rebuilds and the others
/// wait for its lock (ADR-0008).
/// </summary>
internal sealed partial class ReadModelInitializer(
    IServiceScopeFactory scopes,
    ReadModelGate gate,
    ILogger<ReadModelInitializer> logger,
    TimeProvider time) : BackgroundService
{
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield(); // do not run inside the host's start
        var backoff = TimeSpan.FromSeconds(1);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var scope = scopes.CreateAsyncScope();
                await using (scope.ConfigureAwait(false))
                {
                    var readModel = scope.ServiceProvider.GetRequiredService<IAvailabilityReadModel>();
                    if (await readModel.GetLoadedAtAsync(stoppingToken).ConfigureAwait(false) is { } loadedAt)
                    {
                        LogAlreadyLoaded(logger, loadedAt);
                    }
                    else
                    {
                        var rebuilder = scope.ServiceProvider.GetRequiredService<ReadModelRebuilder>();
                        var report = await rebuilder.RebuildAsync(stoppingToken).ConfigureAwait(false);
                        LogRebuilt(logger, report.Facilities, report.Spots, report.Events, report.Duration.TotalMilliseconds);
                    }
                }

                gate.Open();
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // RebuildInProgressException: another Worker (or the command) is rebuilding. Wait and check again:
                // once it finishes, sp:rm:meta exists and this Worker just opens its gate (finding 1).
                if (ex is RebuildInProgressException)
                {
                    LogWaiting(logger, backoff);
                }
                else
                {
                    LogFailed(logger, backoff, ex);
                }

                try
                {
                    await Task.Delay(backoff, time, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
            }
        }
    }

    [LoggerMessage(1, LogLevel.Information, "Availability read model already loaded at {LoadedAt}; no rebuild.")]
    private static partial void LogAlreadyLoaded(ILogger logger, DateTimeOffset loadedAt);

    [LoggerMessage(2, LogLevel.Information,
        "Availability read model rebuilt: {Facilities} facilities, {Spots} spots, {Events} latest events in {Milliseconds} ms.")]
    private static partial void LogRebuilt(ILogger logger, int facilities, int spots, int events, double milliseconds);

    [LoggerMessage(4, LogLevel.Information, "Another rebuild of the availability read model is in progress; checking again in {Backoff}.")]
    private static partial void LogWaiting(ILogger logger, TimeSpan backoff);

    [LoggerMessage(3, LogLevel.Warning, "Initial load of the availability read model failed; retrying in {Backoff}.")]
    private static partial void LogFailed(ILogger logger, TimeSpan backoff, Exception exception);
}
