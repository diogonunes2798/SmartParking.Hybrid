using System.Data.Common;
using Microsoft.Extensions.Options;
using SmartParking.Infrastructure;

namespace SmartParking.Workers.Reservations;

/// <summary>
/// Runs one <see cref="ReservationExpiryPass"/> on start and then one every <c>Reservations:ExpiryScanSeconds</c>
/// (spec 002, «Integrations»). A database error in a pass (for example, PostgreSQL down: a <see cref="DbException"/>,
/// or EF Core's <see cref="InvalidOperationException"/> around one) is logged as a warning and the next pass tries
/// again; any other exception stops the host, the .NET default, so the failure is visible.
/// </summary>
internal sealed partial class ReservationExpiryWorker(
    ReservationExpiryPass pass,
    IOptions<ReservationOptions> options,
    TimeProvider clock,
    ILogger<ReservationExpiryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.Value.ExpiryScanSeconds), clock);

        do
        {
            try
            {
                await pass.RunAsync(stoppingToken);
            }
            catch (DbException exception)
            {
                LogPassFailed(logger, exception);
            }
            catch (InvalidOperationException exception) when (exception.InnerException is DbException)
            {
                // EF Core's execution strategy wraps a transient database failure (for example, a refused
                // connection) in an InvalidOperationException whose inner exception is the provider's DbException.
                LogPassFailed(logger, exception);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "Reservation expiry pass failed; the next pass tries again")]
    private static partial void LogPassFailed(ILogger logger, Exception exception);
}
