using System.CommandLine;
using System.Globalization;
using SmartParking.SensorSimulator;
using SmartParking.SensorSimulator.Provisioning;

namespace SmartParking.Api.Tests.Features.Simulator;

/// <summary>
/// The simulator's command line and provisioning layout without an Api (spec 003, F4b; review of F4b, finding 7):
/// options rejected before any request, a malformed mapping, and the zone split for large parks.
/// </summary>
public sealed class SimulatorCommandTests
{
    [Theory]
    [InlineData("1e309")] // parses as infinity, in any culture
    [InlineData("1e300")] // finite, but TimeSpan.FromSeconds overflows
    [InlineData("86401")]
    public async Task Command_MeanIntervalNotFiniteOrTooLarge_ExitsWithInvalidOptions(string meanInterval)
    {
        // Arrange
        var command = SimulatorCommand.Create();

        // Act
        var exitCode = await command.Parse(["--mean-interval", meanInterval]).InvokeAsync(cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(ExitCodes.InvalidOptions, exitCode);
    }

    [Fact]
    public async Task Command_PeakFactorInfinite_ExitsWithInvalidOptions()
    {
        // Arrange
        var command = SimulatorCommand.Create();

        // Act
        var exitCode = await command.Parse(["--peak-factor", "1e309"]).InvokeAsync(cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(ExitCodes.InvalidOptions, exitCode);
    }

    [Theory]
    [InlineData("{ \"seed\": 42, \"sensors\": [")]
    [InlineData("{ \"seed\": 42 }")]
    [InlineData("{ \"seed\": 42, \"sensors\": [null] }")]
    public async Task Emit_MalformedMapping_ExitsWithMappingNotFound(string content)
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        var mappingFile = Path.Combine(Path.GetTempPath(), $"sensor-map-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(mappingFile, content, cancellationToken);
        var command = SimulatorCommand.Create();

        try
        {
            // Act
            var exitCode = await command.Parse(["--mapping", mappingFile, "--duration", "00:00:01"]).InvokeAsync(cancellationToken: cancellationToken);

            // Assert
            Assert.Equal(ExitCodes.MappingNotFound, exitCode);
        }
        finally
        {
            File.Delete(mappingFile);
        }
    }

    [Fact]
    public void Parse_DecimalPointAndNegativeSeedUnderPortugueseCulture_ReadsInvariantValues()
    {
        // Arrange: pt-PT uses ',' as the decimal separator; the command line must not depend on it (Diogo, 2026-11-26).
        var command = SimulatorCommand.Create();
        string[] args =
        [
            "--mean-interval", "0.5", "--peak-factor", "2.5", "--seed", "-7", "--flush-interval", "00:00:00.5",
            "--duration", "00:00:01.25", "--peak-at", "00:10:00", "--peak-duration", "00:05:00.5",
            "--peak-at", "00:20:00", "--peak-duration", "00:05:00",
        ];

        // Act
        var result = UnderCulture("pt-PT", () => command.Parse(args));

        // Assert
        Assert.Empty(result.Errors);
        Assert.Equal(0.5, result.GetValue<double>("--mean-interval"));
        Assert.Equal(2.5, result.GetValue<double>("--peak-factor"));
        Assert.Equal(-7, result.GetValue<int>("--seed"));
        Assert.Equal(TimeSpan.FromMilliseconds(500), result.GetValue<TimeSpan>("--flush-interval"));
        Assert.Equal(TimeSpan.FromMilliseconds(1_250), result.GetValue<TimeSpan?>("--duration"));
        Assert.Equal([TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(20)], result.GetValue<TimeSpan[]>("--peak-at") ?? []);
        Assert.Equal([TimeSpan.FromMinutes(5) + TimeSpan.FromMilliseconds(500), TimeSpan.FromMinutes(5)], result.GetValue<TimeSpan[]>("--peak-duration") ?? []);
    }

    [Theory]
    [InlineData("--mean-interval", "0,5")]
    [InlineData("--flush-interval", "00:00:00,5")]
    [InlineData("--seed", "4,2")]
    public void Parse_DecimalCommaUnderPortugueseCulture_IsAParseError(string option, string value)
    {
        // Arrange: the same text no longer means a different value on another machine.
        var command = SimulatorCommand.Create();

        // Act
        var result = UnderCulture("pt-PT", () => command.Parse([option, value]));

        // Assert
        var error = Assert.Single(result.Errors);
        Assert.Contains(option, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ZoneRange_LargePark_SplitsEverySpotContiguouslyWithoutOverflow()
    {
        // Arrange: above about 46 000 spots, zone * spots no longer fits in an int.
        const int spots = 100_000;
        const int zones = 5;

        // Act
        var ranges = Enumerable.Range(0, zones).Select(zone => Provisioner.ZoneRange(zone, spots, zones)).ToList();

        // Assert
        Assert.Equal(0, ranges[0].First);
        Assert.Equal(spots, ranges[^1].Last);
        Assert.All(ranges.Zip(ranges.Skip(1)), pair => Assert.Equal(pair.First.Last, pair.Second.First));
        Assert.All(ranges, range => Assert.Equal(spots / zones, range.Last - range.First));
    }

    private static T UnderCulture<T>(string name, Func<T> action)
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
            return action();
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}
