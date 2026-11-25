using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SmartParking.Infrastructure;

namespace SmartParking.Workers.Reservations;

/// <summary>
/// Runs one <see cref="ReservationExpiryPass"/> on start and then one every <c>Reservations:ExpiryScanSeconds</c>
/// (spec 002, «Integrations»). A database error in a pass is logged as a warning and the next pass tries again: a
/// <see cref="DbException"/> (PostgreSQL down when listing), or EF Core's <see cref="InvalidOperationException"/> or
/// <see cref="DbUpdateException"/> around one (a refused connection; a save that fails). Any other exception stops
/// the host, the .NET default, so the failure is visible: for example, a non-conflict error that
/// <see cref="ReservationExpiryPass"/> turns into an <see cref="InvalidOperationException"/> without an inner
/// <see cref="DbException"/>.
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
            catch (DbUpdateException exception) when (exception.InnerException is DbException)
            {
                // A save that fails with a database error the unit of work does not translate (for example, the
                // server restarting mid-transaction) reaches here wrapped by EF Core; the transaction was rolled back.
                LogPassFailed(logger, exception);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "Reservation expiry pass failed; the next pass tries again")]
    private static partial void LogPassFailed(ILogger logger, Exception exception);
}
