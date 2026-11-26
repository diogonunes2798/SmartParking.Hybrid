using System.Globalization;
using SmartParking.Api.Tests.Fixtures;
using SmartParking.SensorSimulator.Generation;

namespace SmartParking.Api.Tests.Features.Simulator;

/// <summary>
/// The simulator's generator (spec 003, F4b): reproducibility, alternation per spot, and the rate inside and outside a
/// peak window (AC-20, AC-21, AC-22). Pure: no Api and no database.
/// </summary>
public sealed class OccupancyGeneratorTests
{
    private static readonly FakeTimeProvider Clock = new(new DateTimeOffset(2026, 12, 2, 10, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Generate_SameMapSeedAndClock_ProducesIdenticalSequences_AC20()
    {
        // Arrange
        var map = SimulatorMaps.Synthetic(50);
        var settings = new GeneratorSettings(42, TimeSpan.FromSeconds(60), 10, [new PeakWindow(TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(5))]);
        var start = Clock.GetUtcNow();
        var window = TimeSpan.FromMinutes(30);

        // Act
        var first = Run(map, settings, start, window);
        var second = Run(map, settings, start, window);
        var otherSeed = Run(map, settings with { Seed = 43 }, start, window);

        // Assert
        Assert.NotEmpty(first);
        Assert.Equal(first, second);
        Assert.Equal(first.Count, first.Select(item => item.EventId).Distinct().Count());
        Assert.All(first, item => Assert.Equal(8, item.EventId.Version));
        Assert.NotEqual(first.Select(item => item.EventId), otherSeed.Select(item => item.EventId));
        Assert.NotEqual(first.Select(item => item.Timestamp), otherSeed.Select(item => item.Timestamp));
    }

    [Fact]
    public void Generate_SameSeedStartedAtAnotherInstant_UsesOtherEventIds_AC20()
    {
        // Arrange: two runs of the same scenario must not collide in the idempotent event log.
        var map = SimulatorMaps.Synthetic(10);
        var settings = new GeneratorSettings(42, TimeSpan.FromSeconds(60), 10, []);
        var start = Clock.GetUtcNow();

        // Act
        var first = Run(map, settings, start, TimeSpan.FromMinutes(10));
        var later = Run(map, settings, start.AddHours(1), TimeSpan.FromMinutes(10));

        // Assert: the same intervals and types relative to the start, other ids.
        Assert.Equal(first.Select(item => (item.Type, item.Timestamp - start)), later.Select(item => (item.Type, item.Timestamp - start.AddHours(1))));
        Assert.Empty(first.Select(item => item.EventId).Intersect(later.Select(item => item.EventId)));
    }

    [Fact]
    public void Generate_NegativeSeedUnderCultureWithOtherMinusSign_ProducesTheInvariantSequence_AC20()
    {
        // Arrange: a culture whose negative sign is U+2212, as some ICU cultures have (review of F4b, finding 6).
        var map = SimulatorMaps.Synthetic(10);
        var settings = new GeneratorSettings(-1, TimeSpan.FromSeconds(60), 10, []);
        var start = Clock.GetUtcNow();
        var window = TimeSpan.FromMinutes(30);
        var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        culture.NumberFormat.NegativeSign = "−";
        var original = CultureInfo.CurrentCulture;

        // Act
        var invariant = Run(map, settings, start, window);
        List<SimulatedEvent> other;
        try
        {
            CultureInfo.CurrentCulture = culture;
            other = Run(map, settings, start, window);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }

        // Assert
        Assert.NotEmpty(invariant);
        Assert.Equal(invariant, other);
    }

    [Fact]
    public void Generate_EventsPerSpot_AlternateOccupiedAndFreed_AC21()
    {
        // Arrange
        var map = SimulatorMaps.Synthetic(100);
        var settings = new GeneratorSettings(7, TimeSpan.FromSeconds(30), 10, [new PeakWindow(TimeSpan.FromMinutes(20), TimeSpan.FromMinutes(10))]);

        // Act
        var events = Run(map, settings, Clock.GetUtcNow(), TimeSpan.FromHours(1));

        // Assert
        var perSpot = events.GroupBy(item => item.SpotId).Select(group => group.OrderBy(item => item.Timestamp).ToList()).ToList();
        Assert.Equal(map.Count, perSpot.Count);
        Assert.All(perSpot, sequence =>
        {
            for (var index = 1; index < sequence.Count; index++)
            {
                Assert.NotEqual(sequence[index - 1].Type, sequence[index].Type);
                Assert.True(sequence[index].Timestamp > sequence[index - 1].Timestamp);
            }
        });

        // The initial state is drawn with the seed, so both first types occur.
        Assert.Contains(perSpot, sequence => sequence[0].Type == SimulatedEventType.Occupied);
        Assert.Contains(perSpot, sequence => sequence[0].Type == SimulatedEventType.Freed);
    }

    [Fact]
    public void Generate_EventTimestamps_AreUtcWholeMillisecondsInTheIngestionFormat_AC21()
    {
        // Arrange: a start with sub-millisecond ticks.
        var start = Clock.GetUtcNow().AddTicks(1234);
        var settings = new GeneratorSettings(1, TimeSpan.FromSeconds(1), 10, []);

        // Act
        var events = Run(SimulatorMaps.Synthetic(5), settings, start, TimeSpan.FromSeconds(30));

        // Assert
        Assert.All(events, item =>
        {
            Assert.Equal(0, item.Timestamp.UtcTicks % TimeSpan.TicksPerMillisecond);
            Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$", item.ToRequest().Timestamp);
        });
    }

    [Fact]
    public void Generate_PeakWindow_RateRatioWithinPoissonInterval_AC22()
    {
        // Arrange: N spots over a long simulated time with one peak window.
        const int spots = 200;
        const double alpha = 1e-6; // two-sided level of the intervals below; the test fails by chance with at most this probability
        var mean = TimeSpan.FromSeconds(60);
        const double factor = 10;
        var horizon = TimeSpan.FromHours(3);
        var window = new PeakWindow(TimeSpan.FromHours(1), TimeSpan.FromMinutes(10));
        var settings = new GeneratorSettings(42, mean, factor, [window]);
        var start = Clock.GetUtcNow();

        // Act
        var events = Run(SimulatorMaps.Synthetic(spots), settings, start, horizon);

        // Assert
        var inside = events.Count(item => window.Contains(item.Timestamp - start));
        var outside = events.Count - inside;
        var timeInside = window.Duration.TotalSeconds;
        var timeOutside = (horizon - window.Duration).TotalSeconds;

        // Each spot is a Poisson process, so the totals are Poisson: outside with mean N·T_out/mean, and, given the
        // total n, the count inside is Binomial(n, p) with p = factor·T_in / (factor·T_in + T_out).
        var (outsideLow, outsideHigh) = PoissonInterval(spots * timeOutside / mean.TotalSeconds, alpha);
        Assert.InRange(outside, outsideLow, outsideHigh);

        var p = factor * timeInside / (factor * timeInside + timeOutside);
        var (insideLow, insideHigh) = BinomialInterval(events.Count, p, alpha);
        var ratio = RateRatio(inside, events.Count - inside, timeInside, timeOutside);
        Assert.InRange(
            ratio,
            RateRatio(insideLow, events.Count - insideLow, timeInside, timeOutside),
            RateRatio(insideHigh, events.Count - insideHigh, timeInside, timeOutside));
    }

    [Fact]
    public void Generate_NoPeakWindow_RateEqualsTheMeanIntervalWithinPoissonInterval_AC22()
    {
        // Arrange
        const int spots = 100;
        const double alpha = 1e-6;
        var mean = TimeSpan.FromSeconds(30);
        var horizon = TimeSpan.FromHours(2);
        var settings = new GeneratorSettings(5, mean, 10, []);

        // Act
        var events = Run(SimulatorMaps.Synthetic(spots), settings, Clock.GetUtcNow(), horizon);

        // Assert
        var (low, high) = PoissonInterval(spots * horizon.TotalSeconds / mean.TotalSeconds, alpha);
        Assert.InRange(events.Count, low, high);
    }

    private static List<SimulatedEvent> Run(
        IReadOnlyList<SmartParking.SensorSimulator.Provisioning.SensorMapEntry> map, GeneratorSettings settings, DateTimeOffset start, TimeSpan window)
    {
        var end = start + window;
        return [.. OccupancyGenerator.Generate(map, settings, start).TakeWhile(item => item.Timestamp < end)];
    }

    private static double RateRatio(int inside, int outside, double timeInside, double timeOutside) =>
        inside / timeInside / (outside / timeOutside);

    /// <summary>Central interval of Poisson(<paramref name="mean"/>): the counts outside it have probability at most α.</summary>
    private static (int Low, int High) PoissonInterval(double mean, double alpha)
    {
        var upper = (int)Math.Ceiling(mean + 20 * Math.Sqrt(mean) + 20);
        var logPmf = new double[upper + 1];
        logPmf[0] = -mean;
        for (var k = 1; k <= upper; k++)
        {
            logPmf[k] = logPmf[k - 1] + Math.Log(mean) - Math.Log(k);
        }

        return CentralInterval(logPmf, alpha);
    }

    /// <summary>Central interval of Binomial(<paramref name="trials"/>, <paramref name="p"/>).</summary>
    private static (int Low, int High) BinomialInterval(int trials, double p, double alpha)
    {
        var logPmf = new double[trials + 1];
        logPmf[0] = trials * Math.Log(1 - p);
        for (var k = 0; k < trials; k++)
        {
            logPmf[k + 1] = logPmf[k] + Math.Log(trials - k) - Math.Log(k + 1) + Math.Log(p) - Math.Log(1 - p);
        }

        return CentralInterval(logPmf, alpha);
    }

    /// <summary>The smallest and largest counts whose lower and upper tails each keep at least 1 − α/2 of the mass.</summary>
    private static (int Low, int High) CentralInterval(double[] logPmf, double alpha)
    {
        var max = logPmf.Max();
        var pmf = logPmf.Select(value => Math.Exp(value - max)).ToArray();
        var total = pmf.Sum();

        var low = 0;
        var cumulative = 0.0;
        while (cumulative + (pmf[low] / total) <= alpha / 2)
        {
            cumulative += pmf[low] / total;
            low++;
        }

        var high = pmf.Length - 1;
        cumulative = 0.0;
        while (cumulative + (pmf[high] / total) <= alpha / 2)
        {
            cumulative += pmf[high] / total;
            high--;
        }

        return (low, high);
    }
}
