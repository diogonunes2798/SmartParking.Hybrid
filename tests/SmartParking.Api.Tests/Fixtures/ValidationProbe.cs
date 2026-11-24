namespace SmartParking.Api.Tests.Fixtures;

/// <summary>Counts how many times the validated test handler ran (AC-12).</summary>
internal sealed class ValidationProbe
{
    private int calls;

    public int Calls => Volatile.Read(ref calls);

    public void RecordCall() => Interlocked.Increment(ref calls);
}
