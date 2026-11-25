namespace SmartParking.Api.Tests.Fixtures;

/// <summary>
/// Barrier in front of the unit of work (AC-11, AC-16; plan 002a). Disarmed, it lets every save through, so the test
/// can arrange data. Armed for N participants, it holds each save until N requests have read and reached it, so they
/// all write over the same <c>xmin</c>: the stale-version path is tested for certain, not by chance.
/// </summary>
internal sealed class ConcurrencyGate
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly Lock gate = new();

    private TaskCompletionSource? released;

    private int waiting;

    private int participants;

    /// <summary>
    /// Holds the next <paramref name="participantCount"/> saves. Refuses to arm over a gate still pending, so a test that
    /// failed without releasing it cannot hold or release the saves of the next one.
    /// </summary>
    public void Arm(int participantCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(participantCount, 2);

        lock (gate)
        {
            if (released is not null)
            {
                throw new InvalidOperationException("The concurrency gate is still armed; a previous test did not disarm it.");
            }

            participants = participantCount;
            waiting = 0;
            released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    /// <summary>Releases anyone waiting and lets every later save through. Call it in a <c>finally</c> after arming.</summary>
    public void Disarm()
    {
        lock (gate)
        {
            released?.TrySetResult();
            released = null;
        }
    }

    public async Task ArriveAsync(CancellationToken cancellationToken)
    {
        Task wait;
        lock (gate)
        {
            if (released is null)
            {
                return;
            }

            wait = released.Task;
            if (++waiting == participants)
            {
                released.SetResult();
                released = null;
            }
        }

        await wait.WaitAsync(Timeout, cancellationToken);
    }
}
