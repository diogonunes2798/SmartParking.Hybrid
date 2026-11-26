namespace SmartParking.SensorSimulator.Generation;

/// <summary>
/// Parameters of the reference scenario (Table 9, D8): one event per spot every <see cref="MeanInterval"/> on average
/// and <see cref="PeakFactor"/> times that rate inside the <see cref="PeakWindows"/>. <see cref="Seed"/> makes a run reproducible.
/// </summary>
internal sealed record GeneratorSettings(int Seed, TimeSpan MeanInterval, double PeakFactor, IReadOnlyList<PeakWindow> PeakWindows)
{
    /// <summary>Events per second of one spot at <paramref name="offset"/> from the start of the run.</summary>
    public double RateAt(TimeSpan offset) =>
        (PeakWindows.Any(window => window.Contains(offset)) ? PeakFactor : 1.0) / MeanInterval.TotalSeconds;

    /// <summary>The first window edge strictly after <paramref name="offset"/>, where the rate may change.</summary>
    public TimeSpan? NextBoundaryAfter(TimeSpan offset)
    {
        TimeSpan? next = null;
        foreach (var window in PeakWindows)
        {
            foreach (var edge in (ReadOnlySpan<TimeSpan>)[window.Start, window.End])
            {
                if (edge > offset && (next is null || edge < next))
                {
                    next = edge;
                }
            }
        }

        return next;
    }
}
