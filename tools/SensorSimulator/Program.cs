using System.Globalization;
using System.Text.Json;

namespace SmartParking.SensorSimulator;

/// <summary>
/// SensorSimulator (spec §8; UC-01, UC-05). Usage: see tools/SensorSimulator/README.md.
/// Exit codes: 0 clean run, 1 events rejected or failed, 2 bad arguments, 3 setup failed (provisioning, read model).
/// Not a top-level program: the integration tests reference this assembly and the API, and two global
/// <c>Program</c> types would clash.
/// </summary>
internal static class SimulatorProgram
{
    public static async Task<int> Main(string[] args)
    {
        ScenarioOptions options;
        try
        {
            options = ScenarioOptions.Parse(args);
            var problems = options.Validate();
            if (problems.Count > 0)
            {
                throw new ArgumentException(string.Join(Environment.NewLine, problems));
            }
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException or IOException or FormatException)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }

        var generator = new OccupancyEventGenerator(options);

        if (options.DryRun)
        {
            await using var stdout = Console.OpenStandardOutput();
            using var writer = new Utf8JsonWriter(stdout, new JsonWriterOptions { SkipValidation = true });
            var newline = "\n"u8.ToArray();
            foreach (var e in generator.Generate())
            {
                EventJsonWriter.WriteDryRunLine(writer, e);
                writer.Reset();
                await stdout.WriteAsync(newline);
            }

            return 0;
        }

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancellation.Cancel();
        };

        using var handler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = Math.Max(options.Concurrency, 8),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri(options.Api),
            Timeout = TimeSpan.FromSeconds(options.RequestTimeoutSeconds),
        };

        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Scenario '{options.Name}': {options.Facilities} facilities, {options.Spots} spots, rate {options.Rate} ev/s " +
            $"(peak ×{options.PeakFactor}), {options.DurationSeconds} s, gen2 {options.Gen2RatioAt(0):P0} → {options.Gen2Ratio:P0}, " +
            $"seed {options.Seed}, run {options.Run}, API {options.Api}"));

        Topology topology;
        try
        {
            var provisioner = new Provisioner(http, options, Console.Out);
            topology = await provisioner.ProvisionAsync(cancellation.Token);
            if (!await provisioner.WaitForReadModelAsync(topology, cancellation.Token))
            {
                return 3;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            Console.Error.WriteLine($"Setup failed: {ex.Message}");
            return 3;
        }

        var summary = await new LoadRunner(http, options, topology, Console.Out).RunAsync(generator.Generate(), cancellation.Token);
        Console.WriteLine(summary);
        return summary.Clean ? 0 : 1;
    }
}
