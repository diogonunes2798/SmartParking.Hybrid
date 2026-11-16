using System.Buffers;
using System.Text.Json;
using SmartParking.SensorSimulator;

namespace SmartParking.SensorSimulator.Tests;

/// <summary>Command line, scenario files and the JSON body sent to <c>POST /api/ingest/occupancy</c>.</summary>
public sealed class ScenarioAndJsonTests
{
    [Fact]
    public void Command_line_overrides_the_scenario_file()
    {
        var options = ScenarioOptions.Parse(
            ["--scenario", Path.Combine("scenarios", "smoke.json"), "--rate", "7.5", "--gen2-ratio", "50%", "--dry-run"]);

        Assert.Equal("smoke", options.Name);
        Assert.Equal(50, options.Spots);         // from the file
        Assert.Equal(7.5, options.Rate);         // overridden
        Assert.Equal(0.5, options.Gen2Ratio);    // "50%" accepted
        Assert.True(options.DryRun);             // flag without value
    }

    [Fact]
    public void Manual_test_command_of_phase_7_is_accepted()
    {
        var options = ScenarioOptions.Parse(["--facilities", "2", "--spots", "50", "--rate", "5", "--duration", "60", "--seed", "42"]);
        Assert.Empty(options.Validate());
        Assert.Equal((2, 50, 5.0, 60.0, 42L), (options.Facilities, options.Spots, options.Rate, options.DurationSeconds, options.Seed));
    }

    [Theory]
    [InlineData("--spots", "0")]
    [InlineData("--rate", "-1")]
    [InlineData("--gen2-ratio", "1.5")]
    [InlineData("--batch-size", "1001")]
    [InlineData("--peak-factor", "0.5")]
    public void Invalid_values_are_reported(string option, string value)
    {
        Assert.NotEmpty(ScenarioOptions.Parse([option, value]).Validate());
    }

    [Fact]
    public void Unknown_option_is_an_error()
    {
        Assert.Throws<ArgumentException>(() => ScenarioOptions.Parse(["--sensors", "5000"]));
    }

    [Theory]
    [InlineData("smoke.json")]
    [InlineData("d8-reference.json")]
    [InlineData("schema-evolution-pilot.json")]
    public void Versioned_scenarios_load_and_are_valid(string file)
    {
        var options = ScenarioOptions.Load(Path.Combine("scenarios", file));
        Assert.Empty(options.Validate());
    }

    [Fact]
    public void D8_reference_scenario_has_the_closed_values()
    {
        var d8 = ScenarioOptions.Load(Path.Combine("scenarios", "d8-reference.json"));
        Assert.Equal(5000, d8.Spots);
        Assert.Equal(300, d8.Spots / d8.Rate, precision: 1);   // one event per spot every 5 min
        Assert.Equal(10, d8.PeakFactor);
    }

    [Fact]
    public void Gen2_ratio_ramp_is_linear_between_start_and_end()
    {
        var o = new ScenarioOptions { Gen2InitialRatio = 0, Gen2Ratio = 0.5, Gen2RampStartSeconds = 300, Gen2RampDurationSeconds = 60 };
        Assert.Equal(0, o.Gen2RatioAt(299.9));
        Assert.Equal(0.25, o.Gen2RatioAt(330), precision: 6);
        Assert.Equal(0.5, o.Gen2RatioAt(360));
        Assert.Equal(0.5, (o with { Gen2RampStartSeconds = null }).Gen2RatioAt(0)); // no ramp: constant target
    }

    [Fact]
    public void Event_json_has_the_minimal_schema_and_the_gen2_payload()
    {
        var options = new ScenarioOptions { Spots = 4, Facilities = 2, Gen2Ratio = 1, Seed = 5, Rate = 10, DurationSeconds = 5 };
        var e = new OccupancyEventGenerator(options).Generate().First();
        var topology = OccupancyEventGeneratorTests.FakeTopology(options);
        var start = new DateTimeOffset(2026, 11, 16, 10, 0, 0, TimeSpan.Zero);

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = EventJsonWriter.CreateWriter(buffer))
        {
            EventJsonWriter.WriteBatch(writer, [e], topology, start);
        }

        using var json = JsonDocument.Parse(buffer.WrittenMemory);
        var item = json.RootElement.EnumerateArray().Single();
        Assert.Equal(e.EventId, item.GetProperty("eventId").GetGuid());
        Assert.Equal(topology.SensorIds[e.SpotIndex], item.GetProperty("sensorId").GetGuid());
        Assert.Equal(topology.SpotIds[e.SpotIndex], item.GetProperty("spotId").GetGuid());
        Assert.Equal(topology.FacilityIds[e.FacilityIndex], item.GetProperty("facilityId").GetGuid());
        Assert.Equal("OCCUPIED", item.GetProperty("type").GetString());
        Assert.Equal(start + e.Offset, item.GetProperty("timestamp").GetDateTimeOffset());
        Assert.Matches("(Z|[+-]\\d{2}:\\d{2})$", item.GetProperty("timestamp").GetString()!); // explicit offset (RF-01)

        var payload = item.GetProperty("payload");
        Assert.Equal(2, payload.GetProperty("sensorGeneration").GetInt32());
        Assert.Equal(e.Readings.Temperature, payload.GetProperty("temperature").GetDouble());
        Assert.Equal(e.Readings.AirQuality, payload.GetProperty("airQuality").GetInt32());
        Assert.Equal(e.Readings.Noise, payload.GetProperty("noise").GetDouble());
        // Always one decimal place, so MongoDB stores a double even for a round value.
        Assert.Contains('.', payload.GetProperty("temperature").GetRawText());
        Assert.Contains('.', payload.GetProperty("noise").GetRawText());
    }

    [Fact]
    public void Run_is_marked_saturated_when_the_offered_load_was_not_kept()
    {
        // Phase 7 allocation review, finding 2: waiting on the channel or a dispatch lag beyond window + 50 ms.
        var options = new ScenarioOptions { BatchWindowMs = 100 };
        RunSummary Summary(Action<RunCounters> record)
        {
            var counters = new RunCounters();
            record(counters);
            return counters.ToSummary(options, TimeSpan.FromSeconds(1), TimeSpan.Zero, 0, (0, 0, 0), 0, cancelled: false);
        }

        Assert.False(Summary(c => c.RecordDispatchLag(TimeSpan.FromMilliseconds(149).Ticks)).Saturated);
        Assert.True(Summary(c => c.RecordDispatchLag(TimeSpan.FromMilliseconds(151).Ticks)).Saturated);
        Assert.True(Summary(c => c.RecordBackpressure(TimeSpan.FromMilliseconds(1))).Saturated);
    }

    [Fact]
    public void Cancelled_run_is_never_clean()
    {
        var summary = new RunCounters().ToSummary(new ScenarioOptions(), TimeSpan.FromSeconds(1), TimeSpan.Zero, 0, (0, 0, 0), 0, cancelled: true);
        Assert.False(summary.Clean);
        Assert.Contains("CANCELLED", summary.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Generation_one_payload_has_no_environmental_fields()
    {
        var options = new ScenarioOptions { Spots = 4, Gen2Ratio = 0, Rate = 10, DurationSeconds = 5 };
        var e = new OccupancyEventGenerator(options).Generate().First();
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = EventJsonWriter.CreateWriter(buffer))
        {
            EventJsonWriter.WriteBatch(writer, [e], OccupancyEventGeneratorTests.FakeTopology(options), DateTimeOffset.UnixEpoch);
        }

        using var json = JsonDocument.Parse(buffer.WrittenMemory);
        var payload = json.RootElement[0].GetProperty("payload");
        Assert.Equal("""{"sensorGeneration":1}""", payload.GetRawText());
    }
}
