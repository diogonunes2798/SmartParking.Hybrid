using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;

namespace SmartParking.SensorSimulator;

/// <summary>Result of one run, printed at the end (and the exit code: 0 only for a clean, complete run).</summary>
/// <param name="RequestP95Ms">Service time: request sent → answer.</param>
/// <param name="ScheduledP95Ms">
/// Scheduled time of the batch's first event → answer. Includes the batch window, the wait in the channel and the
/// service time, so it does not hide a saturated API (no coordinated omission; Phase 7 review, finding 1).
/// </param>
/// <param name="Saturated">
/// The dispatcher had to wait for the senders, or an event left more than one batch window plus 50 ms after its
/// scheduled time: the offered load was not kept, and the run measures the simulator's limit, not the API's.
/// </param>
internal sealed record RunSummary(
    string Scenario,
    long Seed,
    int Run,
    TimeSpan Elapsed,
    long Sent,
    long SentGen1,
    long SentGen2,
    long Accepted,
    long Rejected,
    long DuplicatesInBatch,
    long Errors,
    long NoResponse,
    long Requests,
    IReadOnlyDictionary<int, long> StatusCodes,
    double RequestP50Ms,
    double RequestP95Ms,
    double RequestMaxMs,
    double ScheduledP50Ms,
    double ScheduledP95Ms,
    double ScheduledMaxMs,
    double DispatchLagMaxMs,
    double BackpressureMs,
    bool Saturated,
    bool Cancelled,
    TimeSpan Cpu,
    long AllocatedBytes,
    (int Gen0, int Gen1, int Gen2) Collections,
    long PeakWorkingSetBytes,
    string? FirstError)
{
    public bool Clean => Rejected == 0 && Errors == 0 && NoResponse == 0 && !Cancelled;

    public override string ToString()
    {
        var c = CultureInfo.InvariantCulture;
        var cores = Environment.ProcessorCount;
        var cpuShare = Elapsed.TotalSeconds > 0 ? Cpu.TotalSeconds / Elapsed.TotalSeconds : 0;
        var sb = new StringBuilder();
        sb.AppendLine(c, $"--- Summary: scenario '{Scenario}', seed {Seed}, run {Run}, {Elapsed.TotalSeconds:F1} s{(Cancelled ? " (CANCELLED: partial numbers)" : "")} ---");
        sb.AppendLine(c, $"Sent:       {Sent} events (generation 1: {SentGen1}, generation 2: {SentGen2}) in {Requests} requests");
        sb.AppendLine(c, $"Accepted:   {Accepted} (202 from the API; not proof of storage: a repeated --run is dropped as duplicate downstream)");
        sb.AppendLine(c, $"Rejected:   {Rejected} (RF-01 validation, 202 partial or 400)");
        sb.AppendLine(c, $"Errors:     {Errors + NoResponse} ({Errors} events in non-202/400 answers, {NoResponse} events without an answer)");
        if (DuplicatesInBatch > 0)
        {
            sb.AppendLine(c, $"Duplicates in batch: {DuplicatesInBatch}");
        }

        sb.AppendLine(c, $"HTTP status: {string.Join(", ", StatusCodes.OrderBy(p => p.Key).Select(p => $"{p.Key}×{p.Value}"))}");
        sb.AppendLine(c, $"Request latency (service): p50 {RequestP50Ms:F1} ms, p95 {RequestP95Ms:F1} ms, max {RequestMaxMs:F1} ms");
        sb.AppendLine(c, $"Scheduled → answer:        p50 {ScheduledP50Ms:F1} ms, p95 {ScheduledP95Ms:F1} ms, max {ScheduledMaxMs:F1} ms");
        sb.AppendLine(c, $"Dispatch lag max: {DispatchLagMaxMs:F1} ms (includes the batch window); waiting on full channel: {BackpressureMs:F0} ms");
        sb.AppendLine(Saturated
            ? "Offered load: NOT KEPT (simulator saturated: raise --concurrency or --batch-size; do not use this run as an API measurement)"
            : "Offered load: kept (open loop)");
        sb.AppendLine(c, $"Simulator CPU: {Cpu.TotalSeconds:F2} s = {cpuShare * 100:F1} % of one core ({cpuShare / cores * 100:F2} % of {cores} logical cores)");
        sb.AppendLine(c, $"Simulator memory: {AllocatedBytes / 1048576.0:F1} MiB allocated, GC {Collections.Gen0}/{Collections.Gen1}/{Collections.Gen2}, peak working set {PeakWorkingSetBytes / 1048576.0:F0} MiB (process lifetime, includes provisioning)");
        if (FirstError is not null)
        {
            sb.AppendLine(c, $"First error: {FirstError}");
        }

        return sb.ToString();
    }
}

/// <summary>Thread-safe counters of a run.</summary>
internal sealed class RunCounters
{
    private readonly ConcurrentDictionary<int, long> _statusCodes = new();
    private readonly ConcurrentQueue<double> _requestMs = new();
    private readonly ConcurrentQueue<double> _scheduledMs = new();
    private long _sent;
    private long _sentGen2;
    private long _accepted;
    private long _rejected;
    private long _duplicates;
    private long _errors;
    private long _noResponse;
    private long _maxDispatchLagTicks;
    private long _backpressureTicks;
    private string? _firstError;

    public void RecordSent(ReadOnlySpan<SimulatedEvent> events)
    {
        var gen2 = 0;
        foreach (ref readonly var e in events)
        {
            if (e.Generation == 2)
            {
                gen2++;
            }
        }

        Interlocked.Add(ref _sent, events.Length);
        Interlocked.Add(ref _sentGen2, gen2);
    }

    public void RecordDispatchLag(long ticks)
    {
        var current = Volatile.Read(ref _maxDispatchLagTicks);
        while (ticks > current)
        {
            var seen = Interlocked.CompareExchange(ref _maxDispatchLagTicks, ticks, current);
            if (seen == current)
            {
                return;
            }

            current = seen;
        }
    }

    public void RecordBackpressure(TimeSpan blocked) => Interlocked.Add(ref _backpressureTicks, blocked.Ticks);

    public void RecordStatus(HttpStatusCode status, TimeSpan service, TimeSpan sinceScheduled)
    {
        _statusCodes.AddOrUpdate((int)status, 1, (_, n) => n + 1);
        _requestMs.Enqueue(service.TotalMilliseconds);
        _scheduledMs.Enqueue(sinceScheduled.TotalMilliseconds);
    }

    public void RecordAccepted(int accepted, int rejected, int duplicates)
    {
        Interlocked.Add(ref _accepted, accepted);
        Interlocked.Add(ref _rejected, rejected);
        Interlocked.Add(ref _duplicates, duplicates);
    }

    public void RecordErrors(int count, Exception? error)
    {
        Interlocked.Add(ref _errors, count);
        if (error is not null)
        {
            RecordFirstError(error);
        }
    }

    public void RecordNoResponse(int count, TimeSpan service, TimeSpan sinceScheduled, Exception error)
    {
        Interlocked.Add(ref _noResponse, count);
        _statusCodes.AddOrUpdate(0, 1, (_, n) => n + 1); // 0 = no answer
        _requestMs.Enqueue(service.TotalMilliseconds);
        _scheduledMs.Enqueue(sinceScheduled.TotalMilliseconds);
        RecordFirstError(error);
    }

    public void RecordSenderFailure(Exception error) => RecordFirstError(error);

    private void RecordFirstError(Exception error) =>
        Interlocked.CompareExchange(ref _firstError, $"{error.GetType().Name}: {error.Message}", null);

    public string Progress(TimeSpan elapsed) => string.Create(
        CultureInfo.InvariantCulture,
        $"[{elapsed.TotalSeconds,6:F0} s] sent {Interlocked.Read(ref _sent)}, accepted {Interlocked.Read(ref _accepted)}, " +
        $"rejected {Interlocked.Read(ref _rejected)}, errors {Interlocked.Read(ref _errors) + Interlocked.Read(ref _noResponse)}");

    public RunSummary ToSummary(
        ScenarioOptions options, TimeSpan elapsed, TimeSpan cpu, long allocated, (int, int, int) collections, long peakWorkingSet, bool cancelled)
    {
        var service = Sorted(_requestMs);
        var scheduled = Sorted(_scheduledMs);
        var lagMs = TimeSpan.FromTicks(_maxDispatchLagTicks).TotalMilliseconds;
        var backpressureMs = TimeSpan.FromTicks(_backpressureTicks).TotalMilliseconds;
        return new RunSummary(
            options.Name,
            options.Seed,
            options.Run,
            elapsed,
            _sent,
            _sent - _sentGen2,
            _sentGen2,
            _accepted,
            _rejected,
            _duplicates,
            _errors,
            _noResponse,
            service.Length,
            new Dictionary<int, long>(_statusCodes),
            Percentile(service, 0.50),
            Percentile(service, 0.95),
            service.Length == 0 ? 0 : service[^1],
            Percentile(scheduled, 0.50),
            Percentile(scheduled, 0.95),
            scheduled.Length == 0 ? 0 : scheduled[^1],
            lagMs,
            backpressureMs,
            Saturated: backpressureMs > 0 || lagMs > options.BatchWindowMs + 50,
            cancelled,
            cpu,
            allocated,
            collections,
            peakWorkingSet,
            _firstError);
    }

    private static double[] Sorted(ConcurrentQueue<double> values)
    {
        var array = values.ToArray();
        Array.Sort(array);
        return array;
    }

    /// <summary>Nearest-rank percentile of sorted values.</summary>
    internal static double Percentile(double[] sorted, double p) =>
        sorted.Length == 0 ? 0 : sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1)];
}
