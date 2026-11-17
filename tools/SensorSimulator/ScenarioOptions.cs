using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SmartParking.SensorSimulator;

/// <summary>A time window, in seconds from the start of the run, during which the rate is multiplied by the peak factor.</summary>
internal sealed record PeakWindow(double StartSeconds, double DurationSeconds);

/// <summary>
/// One simulator scenario (spec §8). A JSON file in <c>bench/scenarios/</c> groups the values; command-line options
/// override the file. Units: seconds and events per second. See <c>tools/SensorSimulator/README.md</c>.
/// </summary>
internal sealed record ScenarioOptions
{
    public string Name { get; init; } = "adhoc";

    /// <summary>Base URL of SmartParking.Api.</summary>
    public string Api { get; init; } = "http://localhost:5080";

    /// <summary>Number of parking facilities. Spots are split among them as evenly as possible.</summary>
    public int Facilities { get; init; } = 1;

    /// <summary>Total number of spots (one sensor each), across all facilities. D8: 5 000.</summary>
    public int Spots { get; init; } = 100;

    /// <summary>
    /// Off-peak mean event rate, in events per second for the whole city (Poisson arrivals). D8: 5 000 spots × one
    /// event per spot every 300 s = 16.67 events/s.
    /// </summary>
    public double Rate { get; init; } = 1;

    /// <summary>Rate multiplier inside <see cref="PeakWindows"/>. D8: 10.</summary>
    public double PeakFactor { get; init; } = 1;

    /// <summary>
    /// Peak windows. When <see cref="PeakFactor"/> is above 1 and no window is given, one window covering the middle
    /// 10 % of the run is used.
    /// </summary>
    public IReadOnlyList<PeakWindow> PeakWindows { get; init; } = [];

    public double DurationSeconds { get; init; } = 60;

    /// <summary>Share of spots with a second-generation sensor (UC-05), in [0, 1], once the ramp has finished.</summary>
    public double Gen2Ratio { get; init; }

    /// <summary>Share of second-generation sensors before the ramp starts. Ignored without a ramp.</summary>
    public double Gen2InitialRatio { get; init; }

    /// <summary>When the share starts to move from <see cref="Gen2InitialRatio"/> to <see cref="Gen2Ratio"/>; null = no ramp.</summary>
    public double? Gen2RampStartSeconds { get; init; }

    /// <summary>Length of the linear ramp; 0 = a step change.</summary>
    public double Gen2RampDurationSeconds { get; init; }

    public long Seed { get; init; } = 42;

    /// <summary>
    /// Run number (Phase 9 repeats each scenario at least 5 times). It changes only the <c>eventId</c>s, so that a
    /// repeated run against the same database is not dropped as duplicate; times, spots, types and readings stay
    /// the same for a given seed.
    /// </summary>
    public int Run { get; init; } = 1;

    /// <summary>Events per HTTP request (the API accepts up to 1 000).</summary>
    public int BatchSize { get; init; } = 500;

    /// <summary>How often the due events are collected and sent.</summary>
    public int BatchWindowMs { get; init; } = 100;

    /// <summary>Requests in flight at the same time.</summary>
    public int Concurrency { get; init; } = 4;

    public int RequestTimeoutSeconds { get; init; } = 10;

    /// <summary>Create the missing facilities, spots and sensors through the RF-06 API before sending.</summary>
    public bool Provision { get; init; } = true;

    /// <summary>How long to wait for the read model to know every simulated spot after provisioning; 0 = do not wait.</summary>
    public int WaitReadModelSeconds { get; init; } = 120;

    /// <summary>
    /// At the end, send FREED for every spot left OCCUPIED, so that each run starts and ends with all spots free and the
    /// next run (same database) stays coherent.
    /// </summary>
    public bool FreeAtEnd { get; init; } = true;

    /// <summary>
    /// CSV file to which the run summary is appended, in the format of spec §6 (cenário, variante, métrica, valor,
    /// unidade, execução, configuração); null = console only. Convention: <c>results/&lt;scenario&gt;/&lt;yyyy-MM-dd_HHmm&gt;.csv</c>.
    /// </summary>
    public string? SummaryCsv { get; init; }

    /// <summary>Variant under test (spec §7): <c>hybrid</c> or <c>monolithic</c>. Only labels the CSV.</summary>
    public string Variant { get; init; } = "hybrid";

    /// <summary>
    /// Sensor time of offset 0 (Phase 10, retention scenario): ISO 8601 instant in the past. Null = the moment the run
    /// starts (sensor time = real time, as in every scenario before Phase 10).
    /// </summary>
    public DateTimeOffset? SensorStart { get; init; }

    /// <summary>
    /// Sensor seconds per real second (Phase 10): with 100, a 1 000 s run covers 100 000 s of sensor time, so a load of
    /// 10^6 events can span several past days. Only the timestamps change; the sending schedule and the rate stay real.
    /// Default 1.
    /// </summary>
    public double TimeScale { get; init; } = 1;

    /// <summary>Print the event sequence as NDJSON instead of sending it (no HTTP).</summary>
    public bool DryRun { get; init; }

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
    };

    /// <summary>The peak windows actually used (see <see cref="PeakWindows"/>).</summary>
    public IReadOnlyList<PeakWindow> EffectivePeakWindows =>
        PeakFactor > 1 && (PeakWindows is null || PeakWindows.Count == 0)
            ? [new PeakWindow(DurationSeconds * 0.45, DurationSeconds * 0.10)]
            : PeakWindows;

    /// <summary>Event rate at <paramref name="seconds"/> from the start, given <see cref="EffectivePeakWindows"/>.</summary>
    public double RateAt(double seconds, IReadOnlyList<PeakWindow> peakWindows)
    {
        ArgumentNullException.ThrowIfNull(peakWindows);
        for (var i = 0; i < peakWindows.Count; i++) // no enumerator allocation on the hot path
        {
            var w = peakWindows[i];
            if (seconds >= w.StartSeconds && seconds < w.StartSeconds + w.DurationSeconds)
            {
                return Rate * PeakFactor;
            }
        }

        return Rate;
    }

    /// <summary>Share of second-generation sensors at <paramref name="seconds"/> from the start.</summary>
    public double Gen2RatioAt(double seconds)
    {
        if (Gen2RampStartSeconds is not { } start)
        {
            return Gen2Ratio;
        }

        if (seconds < start)
        {
            return Gen2InitialRatio;
        }

        if (Gen2RampDurationSeconds <= 0 || seconds >= start + Gen2RampDurationSeconds)
        {
            return Gen2Ratio;
        }

        var progress = (seconds - start) / Gen2RampDurationSeconds;
        return Gen2InitialRatio + ((Gen2Ratio - Gen2InitialRatio) * progress);
    }

    /// <summary>
    /// Every parameter that shapes the run, in one line without commas (a CSV cell). Also salts the event ids, so that
    /// two runs with different parameters never share ids (see <see cref="OccupancyEventGenerator"/>).
    /// </summary>
    public string Describe()
    {
        var windows = string.Join('|', (PeakWindows ?? []).Select(w => string.Create(CultureInfo.InvariantCulture, $"{w.StartSeconds}+{w.DurationSeconds}")));

        // Phase 10, only when set: the ids of every earlier scenario stay as they were.
        var sensorClock =
            (SensorStart is { } start ? string.Create(CultureInfo.InvariantCulture, $";sensorStart={start.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}") : string.Empty) +
            (TimeScale != 1 ? string.Create(CultureInfo.InvariantCulture, $";timeScale={TimeScale}") : string.Empty);
        return sensorClock.Length == 0 ? Core() : Core() + sensorClock;

        string Core() => string.Create(
            CultureInfo.InvariantCulture,
            $"name={Name};facilities={Facilities};spots={Spots};rate={Rate};peak={PeakFactor};windows={windows};duration={DurationSeconds};" +
            $"gen2={Gen2InitialRatio}->{Gen2Ratio}@{Gen2RampStartSeconds?.ToString(CultureInfo.InvariantCulture) ?? "none"}+{Gen2RampDurationSeconds};" +
            $"seed={Seed};batch={BatchSize};window={BatchWindowMs};concurrency={Concurrency};freeAtEnd={FreeAtEnd}");
    }

    /// <summary>Returns the problems of this scenario (empty when valid).</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        void Check(bool ok, string message)
        {
            if (!ok)
            {
                problems.Add(message);
            }
        }

        Check(Facilities >= 1, "facilities must be at least 1.");
        Check(Spots >= Facilities, "spots must be at least the number of facilities.");
        Check(Rate > 0 && double.IsFinite(Rate), "rate must be a positive number.");
        Check(PeakFactor >= 1 && double.IsFinite(PeakFactor), "peak-factor must be at least 1.");
        Check(DurationSeconds > 0 && double.IsFinite(DurationSeconds), "duration must be positive.");
        Check(Gen2Ratio is >= 0 and <= 1, "gen2-ratio must be between 0 and 1.");
        Check(Gen2InitialRatio is >= 0 and <= 1, "gen2-initial-ratio must be between 0 and 1.");
        Check(Gen2RampStartSeconds is null or >= 0, "gen2-ramp-start must not be negative.");
        Check(Gen2RampDurationSeconds >= 0, "gen2-ramp-duration must not be negative.");
        Check(
            Gen2RampStartSeconds is null || Gen2InitialRatio <= Gen2Ratio,
            "gen2-initial-ratio must not exceed gen2-ratio: a second-generation sensor never goes back to generation 1.");
        Check(BatchSize is >= 1 and <= 1000, "batch-size must be between 1 and 1000 (API limit).");
        Check(BatchWindowMs >= 10, "batch-window-ms must be at least 10.");
        Check(Concurrency >= 1, "concurrency must be at least 1.");
        Check(RequestTimeoutSeconds >= 1, "request-timeout must be at least 1.");
        Check(Run >= 1, "run must be at least 1.");
        Check(TimeScale >= 1 && double.IsFinite(TimeScale), "time-scale must be at least 1 (two events of a spot stay >= 1 ms apart).");
        Check(
            SensorStart is null || SensorStart.Value.AddSeconds(DurationSeconds * TimeScale) <= DateTimeOffset.UtcNow.AddMinutes(1),
            "sensor-start + duration × time-scale must not be in the future (the API refuses future timestamps).");
        Check(WaitReadModelSeconds >= 0, "wait-read-model must not be negative.");
        Check(Uri.TryCreate(Api, UriKind.Absolute, out _), "api must be an absolute URL.");
        Check(PeakWindows is not null, "peakWindows must be a list (use [] for none).");
        Check(!string.IsNullOrWhiteSpace(Name) && Name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'),
            "name must use only letters, digits, '-' and '_' (it names facilities and result folders).");
        foreach (var w in PeakWindows ?? [])
        {
            Check(w.StartSeconds >= 0 && w.DurationSeconds > 0, "peak windows need start >= 0 and duration > 0.");
        }

        return problems;
    }

    /// <summary>Reads a scenario file (bench/scenarios/*.json).</summary>
    public static ScenarioOptions Load(string path)
    {
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<ScenarioOptions>(stream, Json)
               ?? throw new InvalidDataException($"Scenario file '{path}' is empty.");
    }

    /// <summary>
    /// Builds the options from the command line: <c>--scenario file.json</c> first (if any), then every other
    /// <c>--name value</c> overrides it. Boolean options accept a value (<c>--provision false</c>) or none (<c>--dry-run</c>).
    /// </summary>
    public static ScenarioOptions Parse(IReadOnlyList<string> args)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Count; i++)
        {
            var key = args[i];
            if (!key.StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Unexpected argument '{key}'. Options start with '--'.");
            }

            string? value = null;
            if (i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                value = args[++i];
            }

            values[key[2..]] = value;
        }

        ScenarioOptions options;
        if (values.Remove("scenario", out var file))
        {
            options = Load(file ?? throw new ArgumentException("Option '--scenario' needs a file path."));
        }
        else
        {
            options = new ScenarioOptions();
        }

        foreach (var (key, value) in values)
        {
            options = Apply(options, key, value);
        }

        return options;
    }

    private static ScenarioOptions Apply(ScenarioOptions o, string key, string? value) => key.ToLowerInvariant() switch
    {
        "name" => o with { Name = Text(key, value) },
        "api" => o with { Api = Text(key, value) },
        "facilities" => o with { Facilities = Int(key, value) },
        "spots" => o with { Spots = Int(key, value) },
        "rate" => o with { Rate = Number(key, value) },
        "peak-factor" => o with { PeakFactor = Number(key, value) },
        "duration" => o with { DurationSeconds = Number(key, value) },
        "gen2-ratio" => o with { Gen2Ratio = Ratio(key, value) },
        "gen2-initial-ratio" => o with { Gen2InitialRatio = Ratio(key, value) },
        "gen2-ramp-start" => o with { Gen2RampStartSeconds = Number(key, value) },
        "gen2-ramp-duration" => o with { Gen2RampDurationSeconds = Number(key, value) },
        "seed" => o with { Seed = Long(key, value) },
        "run" => o with { Run = Int(key, value) },
        "batch-size" => o with { BatchSize = Int(key, value) },
        "batch-window-ms" => o with { BatchWindowMs = Int(key, value) },
        "concurrency" => o with { Concurrency = Int(key, value) },
        "request-timeout" => o with { RequestTimeoutSeconds = Int(key, value) },
        "provision" => o with { Provision = Flag(key, value) },
        "wait-read-model" => o with { WaitReadModelSeconds = Int(key, value) },
        "free-at-end" => o with { FreeAtEnd = Flag(key, value) },
        "dry-run" => o with { DryRun = Flag(key, value) },
        "summary-csv" => o with { SummaryCsv = Text(key, value) },
        "variant" => o with { Variant = Text(key, value) },
        "sensor-start" => o with { SensorStart = Instant(key, value) },
        "time-scale" => o with { TimeScale = Number(key, value) },
        _ => throw new ArgumentException($"Unknown option '--{key}'."),
    };

    private static DateTimeOffset Instant(string key, string? value) =>
        DateTimeOffset.TryParse(Text(key, value), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t)
            ? t
            : throw new ArgumentException($"Option '--{key}' needs an ISO 8601 instant (e.g. 2026-11-12T00:00:00Z).");

    private static string Text(string key, string? value) =>
        value ?? throw new ArgumentException($"Option '--{key}' needs a value.");

    private static long Long(string key, string? value) =>
        long.TryParse(Text(key, value), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            ? n
            : throw new ArgumentException($"Option '--{key}' needs an integer between {long.MinValue} and {long.MaxValue}.");

    private static int Int(string key, string? value) =>
        int.TryParse(Text(key, value), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            ? n
            : throw new ArgumentException($"Option '--{key}' needs an integer.");

    private static double Number(string key, string? value) =>
        double.TryParse(Text(key, value), NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
            ? n
            : throw new ArgumentException($"Option '--{key}' needs a number (use '.' as decimal separator).");

    /// <summary>Accepts 0.5 or 50%.</summary>
    private static double Ratio(string key, string? value)
    {
        var text = Text(key, value);
        return text.EndsWith('%')
            ? Number(key, text[..^1]) / 100
            : Number(key, text);
    }

    private static bool Flag(string key, string? value) => value is null || (bool.TryParse(value, out var b)
        ? b
        : throw new ArgumentException($"Option '--{key}' takes true or false."));
}
