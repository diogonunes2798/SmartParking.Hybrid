using System.CommandLine;
using SmartParking.SensorSimulator.Sending;

namespace SmartParking.SensorSimulator;

/// <summary>
/// Entry point. Not top-level statements: their global <c>Program</c> type would clash with the Api's <c>Program</c>
/// in the tests, which see both assemblies (<c>InternalsVisibleTo</c>).
/// </summary>
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // Ctrl+C cancels the run; the buffered events are flushed and the pending sends get the drain timeout to finish.
        var configuration = new InvocationConfiguration { ProcessTerminationTimeout = GatewayRunner.DrainTimeout + TimeSpan.FromSeconds(5) };
        return await SimulatorCommand.Create().Parse(args).InvokeAsync(configuration);
    }
}
