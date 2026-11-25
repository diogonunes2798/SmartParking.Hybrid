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

    public void Arm(int participantCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(participantCount, 2);

        lock (gate)
        {
            participants = participantCount;
            waiting = 0;
            released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
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
