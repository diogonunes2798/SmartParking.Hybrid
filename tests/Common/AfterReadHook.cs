namespace SmartParking.Tests.Common;

/// <summary>
/// The action of <see cref="AfterReadReservationRepository"/>: armed for one reservation, it runs once, the first time
/// that reservation is read, and then disarms itself. Disarmed, every read goes through untouched.
/// </summary>
internal sealed class AfterReadHook
{
    private readonly Lock gate = new();

    private Guid target;

    private Func<CancellationToken, Task>? action;

    public void Arm(Guid reservationId, Func<CancellationToken, Task> onRead)
    {
        ArgumentNullException.ThrowIfNull(onRead);

        lock (gate)
        {
            target = reservationId;
            action = onRead;
        }
    }

    public void Disarm()
    {
        lock (gate)
        {
            action = null;
        }
    }

    public Task RunOnceAsync(Guid reservationId, CancellationToken cancellationToken)
    {
        Func<CancellationToken, Task>? run;
        lock (gate)
        {
            if (action is null || reservationId != target)
            {
                return Task.CompletedTask;
            }

            run = action;
            action = null;
        }

        return run(cancellationToken);
    }
}
