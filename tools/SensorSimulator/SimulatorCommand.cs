using System.CommandLine;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SmartParking.SensorSimulator.Generation;
using SmartParking.SensorSimulator.Provisioning;
using SmartParking.SensorSimulator.Sending;
using SmartParking.SensorSimulator.Wire;

namespace SmartParking.SensorSimulator;

/// <summary>
/// Command line of the simulator (spec 003, F4b). Without <c>--provision</c> it emits events over the mapping file in
/// real time; with it, it registers the facility through the F2 endpoints and writes the mapping. The defaults are the
/// reference scenario of Table 9 (D8): 5 000 spots, one event per spot every 300 s, peaks of 10×.
/// </summary>
internal static class SimulatorCommand
{
    /// <summary>
    /// Upper bound of <c>--mean-interval</c> (one day). A correctness limit, not a performance value: it keeps
    /// <see cref="TimeSpan.FromSeconds(double)"/> and the generator's offsets far from overflow (review of F4b, finding 7).
    /// </summary>
    public const double MaxMeanIntervalSeconds = 86_400;

    /// <param name="configureServices">Runs after the simulator's own registrations; the tests use it to reach the test server.</param>
    public static RootCommand Create(Action<IServiceCollection>? configureServices = null)
    {
        // Text, not Uri: System.CommandLine 2.0 has no Uri conversion, so the address is checked in Validate.
        Option<string> api = new("--api") { Description = "Base address of the Api", DefaultValueFactory = _ => "http://localhost:5080" };
        Option<bool> provision = new("--provision") { Description = "Register the facility, zones, spots and sensors through the F2 endpoints and write the mapping" };
        Option<FileInfo> mapping = new("--mapping") { Description = "Mapping file (hardwareId–spotId)", DefaultValueFactory = _ => new FileInfo("sensor-map.json") };
        Option<int> spots = new("--spots") { Description = "Spots to provision (Table 9)", DefaultValueFactory = _ => 5_000 };
        Option<int> zones = new("--zones") { Description = "Zones to provision; spots are split across them in order", DefaultValueFactory = _ => 5 };
        Option<int> seed = new("--seed") { Description = "Seed of the layout, initial states, intervals and eventIds", DefaultValueFactory = _ => 42 };
        Option<double> meanInterval = new("--mean-interval") { Description = "Mean seconds between two events of one spot (Table 9)", DefaultValueFactory = _ => 300 };
        Option<double> peakFactor = new("--peak-factor") { Description = "Rate multiplier inside a peak window (Table 9)", DefaultValueFactory = _ => 10 };
        Option<TimeSpan[]> peakAt = new("--peak-at") { Description = "Start of a peak window, as an offset from the start of the run (repeatable)" };
        Option<TimeSpan[]> peakDuration = new("--peak-duration") { Description = "Length of the peak window at the same position (repeatable)" };
        Option<TimeSpan?> duration = new("--duration") { Description = "Length of the run; until Ctrl+C when absent" };
        Option<int> batchSize = new("--batch-size") { Description = "Events per batch (at most 500)", DefaultValueFactory = _ => SenderSettings.Default.BatchSize };
        Option<TimeSpan> flushInterval = new("--flush-interval") { Description = "Longest wait of a buffered event before its batch is sent", DefaultValueFactory = _ => SenderSettings.Default.FlushInterval };
        Option<int> maxRetries = new("--max-retries") { Description = "Resends of a batch after a 503", DefaultValueFactory = _ => SenderSettings.Default.MaxRetries };

        RootCommand root = new("SmartParking sensor simulator: provisions the reference scenario and emits occupancy events (spec 003)")
        {
            api, provision, mapping, spots, zones, seed, meanInterval, peakFactor, peakAt, peakDuration, duration,
            batchSize, flushInterval, maxRetries,
        };

        // Checked in the action: only the ParseResult applies the default values.
        List<string> Validate(ParseResult result)
        {
            var errors = new List<string>();
            if (ApiAddress(result.GetValue(api)) is null)
            {
                errors.Add("--api must be an absolute http or https address.");
            }

            if (result.GetValue(spots) < 1)
            {
                errors.Add("--spots must be at least 1.");
            }

            if (result.GetValue(zones) < 1 || result.GetValue(zones) > result.GetValue(spots))
            {
                errors.Add("--zones must be between 1 and --spots.");
            }

            // Not-a-number fails "> 0" and infinity fails the upper bound (review of F4b, finding 7).
            if (result.GetValue(meanInterval) is not (> 0 and <= MaxMeanIntervalSeconds))
            {
                errors.Add($"--mean-interval must be greater than 0 and at most {MaxMeanIntervalSeconds} seconds.");
            }

            if (result.GetValue(peakFactor) is not > 0 || !double.IsFinite(result.GetValue(peakFactor)))
            {
                errors.Add("--peak-factor must be a finite number greater than 0.");
            }

            if (result.GetValue(batchSize) is < 1 or > OccupancyBatchRequest.MaxEvents)
            {
                errors.Add($"--batch-size must be between 1 and {OccupancyBatchRequest.MaxEvents}.");
            }

            if (result.GetValue(flushInterval) <= TimeSpan.Zero)
            {
                errors.Add("--flush-interval must be greater than 0.");
            }

            if (result.GetValue(maxRetries) < 0)
            {
                errors.Add("--max-retries must be 0 or greater.");
            }

            if (result.GetValue(duration) is { } length && length <= TimeSpan.Zero)
            {
                errors.Add("--duration must be greater than 0.");
            }

            var starts = result.GetValue(peakAt) ?? [];
            var lengths = result.GetValue(peakDuration) ?? [];
            if (starts.Length != lengths.Length)
            {
                errors.Add("Each --peak-at needs one --peak-duration.");
            }

            if (starts.Any(start => start < TimeSpan.Zero) || lengths.Any(window => window <= TimeSpan.Zero))
            {
                errors.Add("Peak windows need --peak-at >= 0 and --peak-duration > 0.");
            }

            if (result.GetValue(mapping) is { } file && IsUnderResults(file))
            {
                errors.Add("--mapping cannot be under results/ (the F12 measurements own it).");
            }

            return errors;
        }

        root.SetAction(async (parse, cancellationToken) =>
        {
            if (Validate(parse) is [_, ..] errors)
            {
                foreach (var error in errors)
                {
                    await Console.Error.WriteLineAsync(error);
                }

                return ExitCodes.InvalidOptions;
            }

            using var host = BuildHost(ApiAddress(parse.GetValue(api))!, configureServices);
            var file = parse.GetValue(mapping)!;

            // Ctrl+C outside the emitting loop (provisioning, loading the mapping) also exits with Cancelled, not with
            // System.CommandLine's 1 for an unhandled exception (second review of F4b).
            try
            {
                if (parse.GetValue(provision))
                {
                    var settings = new ProvisionSettings(parse.GetValue(spots), parse.GetValue(zones), parse.GetValue(seed));
                    return await ProvisionAsync(host.Services, settings, file, cancellationToken);
                }

                var starts = parse.GetValue(peakAt) ?? [];
                var lengths = parse.GetValue(peakDuration) ?? [];
                var generator = new GeneratorSettings(
                    parse.GetValue(seed),
                    TimeSpan.FromSeconds(parse.GetValue(meanInterval)),
                    parse.GetValue(peakFactor),
                    [.. starts.Zip(lengths, (start, length) => new PeakWindow(start, length))]);
                var sender = new SenderSettings(parse.GetValue(batchSize), parse.GetValue(flushInterval), parse.GetValue(maxRetries));
                return await EmitAsync(host.Services, file, generator, sender, parse.GetValue(duration), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return ExitCodes.Cancelled;
            }
        });

        return root;
    }

    private static async Task<int> ProvisionAsync(IServiceProvider services, ProvisionSettings settings, FileInfo file, CancellationToken cancellationToken)
    {
        var outcome = await services.GetRequiredService<Provisioner>().ProvisionAsync(settings, cancellationToken);
        if (outcome.Map is null)
        {
            await Console.Error.WriteLineAsync(outcome.Error);
            // Not resumable (review of F4b, finding 2): the facility name and the hardwareIds are unique, so what was
            // already registered makes a repetition with the same seed answer 409.
            await Console.Error.WriteLineAsync(
                "The facility may be partly registered. Repeat on a clean database, or with another --seed.");
            return ExitCodes.ProvisioningFailed;
        }

        await outcome.Map.SaveAsync(file.FullName, cancellationToken);
        Console.WriteLine(file.FullName);
        return ExitCodes.Success;
    }

    private static async Task<int> EmitAsync(
        IServiceProvider services, FileInfo file, GeneratorSettings generator, SenderSettings sender, TimeSpan? duration, CancellationToken cancellationToken)
    {
        if (!file.Exists)
        {
            await Console.Error.WriteLineAsync($"Mapping not found: {file.FullName}. Run with --provision first.");
            return ExitCodes.MappingNotFound;
        }

        SensorMap? map;
        try
        {
            map = await SensorMap.LoadAsync(file.FullName, cancellationToken);
        }
        catch (JsonException exception)
        {
            // A malformed mapping is a usage error, not a crash (review of F4b, finding 7).
            await Console.Error.WriteLineAsync($"Mapping is not valid: {file.FullName}. {exception.Message}");
            return ExitCodes.MappingNotFound;
        }

        if (map?.Sensors is not { Count: > 0 } || map.Sensors.Any(sensor => sensor is null))
        {
            await Console.Error.WriteLineAsync($"Mapping has no sensors: {file.FullName}.");
            return ExitCodes.MappingNotFound;
        }

        var summary = await services.GetRequiredService<GatewayRunner>().RunAsync(map, generator, sender, duration, cancellationToken);

        // Ctrl+C ends the run with its summary, but the exit code tells it apart from a complete run (review of F4b, finding 5).
        if (cancellationToken.IsCancellationRequested)
        {
            return ExitCodes.Cancelled;
        }

        return summary.NotDeliveredEvents == 0 ? ExitCodes.Success : ExitCodes.NotAllDelivered;
    }

    private static Uri? ApiAddress(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var address) && (address.Scheme == Uri.UriSchemeHttp || address.Scheme == Uri.UriSchemeHttps)
            ? address
            : null;

    private static IHost BuildHost(Uri api, Action<IServiceCollection>? configureServices)
    {
        // System.CommandLine owns the command line; the content root is the tool's folder, never the working directory.
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Services.AddSensorSimulator(api);
        configureServices?.Invoke(builder.Services);
        return builder.Build();
    }

    /// <summary>True when any folder of the path is named <c>results</c> (the measurements of F12; the simulator never writes there).</summary>
    internal static bool IsUnderResults(FileInfo file) =>
        file.Directory is { } directory
        && EnumerateFolders(directory).Any(name => string.Equals(name, "results", StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> EnumerateFolders(DirectoryInfo? directory)
    {
        for (var current = directory; current is not null; current = current.Parent)
        {
            yield return current.Name;
        }
    }
}
