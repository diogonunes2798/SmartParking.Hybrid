using System.Buffers;
using System.Text.Json;
using SmartParking.SensorSimulator;

namespace SmartParking.SensorSimulator.Tests;

/// <summary>Determinism and coherence of the generated sequence (spec §8; Phase 7 required tests 1 and 2).</summary>
public sealed class OccupancyEventGeneratorTests
{
    private static readonly ScenarioOptions Smoke = new()
    {
        Name = "test",
        Facilities = 2,
        Spots = 50,
        Rate = 5,
        DurationSeconds = 600,
        Seed = 42,
        Gen2Ratio = 0.5,
        Gen2InitialRatio = 0,
        Gen2RampStartSeconds = 300,
        Gen2RampDurationSeconds = 60,
    };

    private static List<SimulatedEvent> Run(ScenarioOptions options) => new OccupancyEventGenerator(options).Generate().ToList();

    [Fact]
    public void Same_seed_produces_exactly_the_same_sequence()
    {
        var first = Run(Smoke);
        var second = Run(Smoke);

        Assert.NotEmpty(first);
        Assert.Equal(first, second); // every field: time, spot, facility, type, eventId, generation, readings
        Assert.Equal(Serialise(first), Serialise(second)); // and the JSON sent to the API, byte for byte
    }

    /// <summary>
    /// Golden values of the Phase 7 manual test command (seed 42), recorded on 2026-11-16. Comparing two runs in
    /// one process cannot catch a seed taken from the clock (both runs may read the same tick; mutation M2 of the
    /// Phase 7 diary survived that way); fixed values also pin the sequence across machines and .NET versions.
    /// </summary>
    [Fact]
    public void Seed_42_produces_the_recorded_sequence()
    {
        var options = ScenarioOptions.Parse(["--facilities", "2", "--spots", "50", "--rate", "5", "--duration", "60", "--seed", "42"]);
        var events = Run(options);

        Assert.Equal(308, events.Count);
        Assert.Equal(
            [
                (2_713_551L, 39, 1, true, Guid.Parse("b16ffaf4-b446-49cb-a96f-4cb4d3bc268d")),
                (3_387_922L, 10, 0, true, Guid.Parse("5eab563f-cfa0-48c2-be12-1a2b6c76f783")),
                (7_430_386L, 47, 1, true, Guid.Parse("0f0cad17-bf85-4fa5-8ab2-5237f3598817")),
            ],
            events.Take(3).Select(e => (e.OffsetTicks, e.SpotIndex, e.FacilityIndex, e.Occupied, e.EventId)));
        Assert.Equal(Guid.Parse("59ec29fd-cbee-4168-bb50-5de9c98fa831"), events[^1].EventId);
    }

    [Fact]
    public void Different_seed_produces_a_different_sequence()
    {
        Assert.NotEqual(Run(Smoke), Run(Smoke with { Seed = 43 }));
    }

    [Fact]
    public void A_spot_is_never_occupied_twice_in_a_row()
    {
        // Many events per spot (rate 50 ev/s on 20 spots for 10 min ≈ 1 500 per spot) and a peak window.
        var events = Run(Smoke with { Spots = 20, Rate = 50, PeakFactor = 10 });
        var lastBySpot = new Dictionary<int, bool>();

        foreach (var e in events)
        {
            var previous = lastBySpot.TryGetValue(e.SpotIndex, out var occupied) && occupied;
            Assert.False(previous && e.Occupied, $"spot {e.SpotIndex} OCCUPIED twice in a row at event {e.Sequence}");
            Assert.False(!previous && !e.Occupied, $"spot {e.SpotIndex} FREED while already free at event {e.Sequence}");
            lastBySpot[e.SpotIndex] = e.Occupied;
        }

        Assert.All(lastBySpot.Values, Assert.False); // FreeAtEnd: every spot ends FREE
    }

    [Fact]
    public void Events_are_in_time_order_within_the_duration()
    {
        var events = Run(Smoke);
        Assert.All(events.Zip(events.Skip(1)), p => Assert.True(p.First.OffsetTicks <= p.Second.OffsetTicks));
        Assert.All(events, e => Assert.InRange(e.Offset.TotalSeconds, 0, Smoke.DurationSeconds));
        Assert.Equal(Enumerable.Range(0, events.Count).Select(i => (long)i), events.Select(e => e.Sequence));
    }

    [Fact]
    public void Spot_and_facility_indices_follow_the_topology()
    {
        var map = Topology.MapSpotsToFacilities(3, 10); // 4 + 3 + 3
        Assert.Equal([0, 0, 0, 0, 1, 1, 1, 2, 2, 2], map);
        Assert.All(Run(Smoke with { Facilities = 3, Spots = 10 }), e => Assert.Equal(map[e.SpotIndex], e.FacilityIndex));
    }

    [Fact]
    public void Mean_rate_matches_the_scenario()
    {
        // D8: 5 000 spots, one event per spot every 300 s = 16.67 ev/s. One hour, no peaks, no closing events.
        var d8 = new ScenarioOptions { Spots = 5000, Rate = 5000 / 300.0, DurationSeconds = 3600, Seed = 7, FreeAtEnd = false };
        var count = new OccupancyEventGenerator(d8).Generate().Count();

        // Poisson: mean 60 000, standard deviation ≈ 245; ±4 σ.
        Assert.InRange(count, 60_000 - 1_000, 60_000 + 1_000);
    }

    [Fact]
    public void Peak_window_multiplies_the_rate()
    {
        var options = new ScenarioOptions
        {
            Spots = 1000, Rate = 20, PeakFactor = 10, DurationSeconds = 300, Seed = 3, FreeAtEnd = false,
            PeakWindows = [new PeakWindow(100, 100)],
        };
        var events = Run(options);
        var offPeak = events.Count(e => e.Offset.TotalSeconds < 100);
        var peak = events.Count(e => e.Offset.TotalSeconds is >= 100 and < 200);

        Assert.InRange(offPeak, 2000 - 250, 2000 + 250);   // 20 ev/s × 100 s
        Assert.InRange(peak, 20000 - 800, 20000 + 800);    // 200 ev/s × 100 s
    }

    [Fact]
    public void Default_peak_window_is_the_middle_tenth_of_the_run()
    {
        var options = new ScenarioOptions { PeakFactor = 10, DurationSeconds = 600 };
        Assert.Equal([new PeakWindow(270, 60)], options.EffectivePeakWindows);
        Assert.Empty((options with { PeakFactor = 1 }).EffectivePeakWindows);
    }

    [Fact]
    public void Gen2_ramp_moves_the_share_from_initial_to_target_and_sensors_stay_upgraded()
    {
        var options = Smoke with { Spots = 2000, Rate = 200 };
        var events = Run(options with { FreeAtEnd = false });

        double Share(double from, double to)
        {
            var window = events.Where(e => e.Offset.TotalSeconds >= from && e.Offset.TotalSeconds < to).ToList();
            return window.Count(e => e.Generation == 2) / (double)window.Count;
        }

        Assert.Equal(0, Share(0, 300));                      // before the ramp: 0 %
        Assert.InRange(Share(361, 600), 0.45, 0.55);         // after the ramp: ≈ 50 %

        // Once a sensor reports as generation 2, it never goes back to generation 1.
        var upgraded = new HashSet<int>();
        foreach (var e in events)
        {
            if (e.Generation == 2)
            {
                upgraded.Add(e.SpotIndex);
            }
            else
            {
                Assert.DoesNotContain(e.SpotIndex, upgraded);
            }
        }
    }

    [Fact]
    public void Generation_two_events_carry_the_three_readings_in_range()
    {
        var gen2 = Run(Smoke with { Gen2RampStartSeconds = null, Gen2Ratio = 1 });
        Assert.All(gen2, e =>
        {
            Assert.Equal(2, e.Generation);
            Assert.InRange(e.Readings.Temperature, 5, 35);
            Assert.InRange(e.Readings.AirQuality, 0, 150);
            Assert.InRange(e.Readings.Noise, 35, 85);
        });
    }

    [Fact]
    public void Gen2_share_does_not_change_the_load()
    {
        // Same arrivals, spots, types and ids whatever the share of generation 2 (comparable runs in UC-05).
        static IEnumerable<(long, int, bool, Guid)> Load(IEnumerable<SimulatedEvent> events) =>
            events.Select(e => (e.OffsetTicks, e.SpotIndex, e.Occupied, e.EventId));

        Assert.Equal(Load(Run(Smoke with { Gen2Ratio = 0 })), Load(Run(Smoke with { Gen2Ratio = 1 })));
    }

    [Fact]
    public void Run_number_changes_only_the_event_ids()
    {
        var run1 = Run(Smoke);
        var run2 = Run(Smoke with { Run = 2 });

        Assert.Equal(run1.Select(e => e with { EventId = Guid.Empty }), run2.Select(e => e with { EventId = Guid.Empty }));
        Assert.Empty(run1.Select(e => e.EventId).Intersect(run2.Select(e => e.EventId)));
        Assert.Equal(run1.Count, run1.Select(e => e.EventId).Distinct().Count());
        Assert.All(run1, e => Assert.Equal(4, e.EventId.Version));
    }

    private static byte[] Serialise(IEnumerable<SimulatedEvent> events)
    {
        var topology = FakeTopology(Smoke);
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = EventJsonWriter.CreateWriter(buffer);
        EventJsonWriter.WriteBatch(writer, events.ToArray(), topology, new DateTimeOffset(2026, 11, 16, 10, 0, 0, TimeSpan.Zero));
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>A fixed topology (same ids on every call), standing in for the ids the API would assign.</summary>
    internal static Topology FakeTopology(ScenarioOptions o)
    {
        var ids = new Xoshiro256(seed: 1, stream: 99);
        return new(
            Enumerable.Range(0, o.Facilities).Select(_ => ids.NextGuid()).ToArray(),
            Enumerable.Range(0, o.Spots).Select(_ => ids.NextGuid()).ToArray(),
            Enumerable.Range(0, o.Spots).Select(_ => ids.NextGuid()).ToArray(),
            Topology.MapSpotsToFacilities(o.Facilities, o.Spots));
    }
}
