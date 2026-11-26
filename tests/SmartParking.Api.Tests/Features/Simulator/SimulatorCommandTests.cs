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
}
