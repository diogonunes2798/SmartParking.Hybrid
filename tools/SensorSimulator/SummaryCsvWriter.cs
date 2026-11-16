using System.Globalization;
using System.Text;

namespace SmartParking.SensorSimulator;

/// <summary>
/// Appends a run summary to a CSV in the format of spec §6: cenário, variante, métrica, valor, unidade, execução,
/// configuração. The header is written only when the file is new, so that other scripts of the same scenario
/// (e.g. the D7 latency of <c>bench/schema-evolution/pilot_latency.py</c>) can append to the same file.
/// Gives the RNF-04 counts (errors, requests without an answer) a versioned home outside the diary (review L7).
/// </summary>
internal static class SummaryCsvWriter
{
    public const string Header = "cenario,variante,metrica,valor,unidade,execucao,configuracao";

    public static void Append(string path, ScenarioOptions options, RunSummary s)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(s);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        var isNew = !File.Exists(path) || new FileInfo(path).Length == 0;
        var configuration = options.Describe();
        var rows = new (string Metric, double Value, string Unit)[]
        {
            ("sim_sent", s.Sent, "events"),
            ("sim_sent_gen1", s.SentGen1, "events"),
            ("sim_sent_gen2", s.SentGen2, "events"),
            ("sim_accepted_202", s.Accepted, "events"),
            ("sim_rejected", s.Rejected, "events"),
            ("sim_errors", s.Errors, "events"),
            ("sim_no_response", s.NoResponse, "events"),
            ("sim_requests", s.Requests, "requests"),
            ("sim_request_p50", s.RequestP50Ms, "ms"),
            ("sim_request_p95", s.RequestP95Ms, "ms"),
            ("sim_request_max", s.RequestMaxMs, "ms"),
            ("sim_scheduled_p95", s.ScheduledP95Ms, "ms"),
            ("sim_dispatch_lag_max", s.DispatchLagMaxMs, "ms"),
            ("sim_backpressure", s.BackpressureMs, "ms"),
            ("sim_saturated", s.Saturated ? 1 : 0, "bool"),
            ("sim_cancelled", s.Cancelled ? 1 : 0, "bool"),
            ("sim_elapsed", s.Elapsed.TotalSeconds, "s"),
            ("sim_cpu", s.Cpu.TotalSeconds, "s"),
            ("sim_allocated", s.AllocatedBytes / 1048576.0, "MiB"),
        };

        var sb = new StringBuilder();
        if (isNew)
        {
            sb.Append(Header).Append('\n');
        }

        foreach (var (metric, value, unit) in rows)
        {
            sb.Append(CultureInfo.InvariantCulture,
                $"{options.Name},{options.Variant},{metric},{value.ToString("0.###", CultureInfo.InvariantCulture)},{unit},{options.Run},{configuration}\n");
        }

        File.AppendAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }
}
