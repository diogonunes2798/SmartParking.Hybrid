namespace SmartParking.SensorSimulator.Generation;

/// <summary>
/// A peak window (spec 003, round 6): from <see cref="Start"/> to <see cref="End"/>, offsets from the start of the run,
/// every spot emits at <c>--peak-factor</c> times its normal rate. The windows of each experiment come from the F12 scenarios.
/// </summary>
internal sealed record PeakWindow(TimeSpan Start, TimeSpan Duration)
{
    public TimeSpan End => Start + Duration;

    public bool Contains(TimeSpan offset) => offset >= Start && offset < End;
}
