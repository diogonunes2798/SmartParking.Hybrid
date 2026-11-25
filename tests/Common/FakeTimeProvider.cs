namespace SmartParking.Tests.Common;

/// <summary>
/// Clock the tests control (spec 002; plan 002a, proposal 11). Written by hand so the tests need no new package
/// (<c>Microsoft.Extensions.TimeProvider.Testing</c>). Only <see cref="GetUtcNow"/> is replaced: timers and
/// timestamps keep the system behaviour. Registered only in the hosts that need it; the tests of one collection run
/// one at a time, so a shared instance has no races.
/// </summary>
public sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
{
    private readonly Lock gate = new();

    private DateTimeOffset now = start.ToUniversalTime();

    public override DateTimeOffset GetUtcNow()
    {
        lock (gate)
        {
            return now;
        }
    }

    public void SetUtcNow(DateTimeOffset value)
    {
        lock (gate)
        {
            now = value.ToUniversalTime();
        }
    }

    public void Advance(TimeSpan delta)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(delta, TimeSpan.Zero);

        lock (gate)
        {
            now += delta;
        }
    }
}
