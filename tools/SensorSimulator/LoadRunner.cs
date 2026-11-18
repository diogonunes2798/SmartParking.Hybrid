using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Channels;

namespace SmartParking.SensorSimulator;

/// <summary>
/// Sends the generated events in real time. Every <see cref="ScenarioOptions.BatchWindowMs"/> it collects the events
/// whose sensor time has come, cuts them into batches of at most <see cref="ScenarioOptions.BatchSize"/> and hands
/// them to <see cref="ScenarioOptions.Concurrency"/> senders through a bounded channel.
/// <para>
/// Open-loop check: the throughput ceiling is about <c>Concurrency × BatchSize / request latency</c>. Above it the
/// channel fills, the dispatcher waits and the offered load drops (the run becomes closed-loop). The summary reports
/// the waiting time and the delay from each event's scheduled time to the answer (no coordinated omission), and marks
/// the run as <see cref="RunSummary.Saturated"/>: such a run measured the simulator, not the API (Phase 7 review, 1–3).
/// </para>
/// </summary>
internal sealed class LoadRunner(HttpClient http, ScenarioOptions options, Topology topology, TextWriter log)
{
    private const string IngestPath = "/api/ingest/occupancy";

    private static readonly MediaTypeHeaderValue JsonContentType = new("application/json") { CharSet = "utf-8" };

    private readonly RunCounters _counters = new();
    private readonly Stopwatch _clock = new();

    public async Task<RunSummary> RunAsync(IEnumerable<SimulatedEvent> events, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(events);
        var channel = Channel.CreateBounded<Batch>(new BoundedChannelOptions(options.Concurrency * 2)
        {
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

        var process = Process.GetCurrentProcess();
        var cpuBefore = process.TotalProcessorTime;
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: false);
        var gcBefore = (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));

        // A sender that dies (unexpected exception) cancels the run instead of leaving the dispatcher blocked on a
        // full channel that nobody reads (review finding 4).
        using var failed = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var runStart = options.SensorStart ?? DateTimeOffset.UtcNow; // Phase 10: a past sensor clock for the retention scenario
        _clock.Start();
        var senders = Enumerable.Range(0, options.Concurrency)
            .Select(_ => Task.Run(() => SendLoopGuardedAsync(channel.Reader, runStart, failed), CancellationToken.None))
            .ToArray();

        var cancelled = false;
        try
        {
            await DispatchAsync(events, channel.Writer, failed.Token);
        }
        catch (OperationCanceledException)
        {
            cancelled = true; // Ctrl+C or a failed sender: still print what was measured (review finding 5)
        }
        finally
        {
            channel.Writer.TryComplete();
        }

        try
        {
            await Task.WhenAll(senders);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }

        _clock.Stop();
        process.Refresh();

        return _counters.ToSummary(
            options,
            _clock.Elapsed,
            process.TotalProcessorTime - cpuBefore,
            GC.GetTotalAllocatedBytes(precise: false) - allocatedBefore,
            (GC.CollectionCount(0) - gcBefore.Item1, GC.CollectionCount(1) - gcBefore.Item2, GC.CollectionCount(2) - gcBefore.Item3),
            process.PeakWorkingSet64,
            cancelled);
    }

    private async Task DispatchAsync(IEnumerable<SimulatedEvent> events, ChannelWriter<Batch> writer, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(options.BatchWindowMs));
        using var enumerator = events.GetEnumerator();
        var hasNext = enumerator.MoveNext();
        var lastProgress = TimeSpan.Zero;

        while (hasNext)
        {
            var now = _clock.Elapsed.Ticks;
            Batch? batch = null;
            while (hasNext && enumerator.Current.OffsetTicks <= now)
            {
                batch ??= Batch.Rent(options.BatchSize);
                batch.Add(enumerator.Current);
                _counters.RecordDispatchLag(now - enumerator.Current.OffsetTicks);
                hasNext = enumerator.MoveNext();
                if (batch.Count == options.BatchSize)
                {
                    if (await WriteAsync(writer, batch, ct))
                    {
                        now = _clock.Elapsed.Ticks; // blocked: later events of this tick are later (finding 3)
                    }

                    batch = null;
                }
            }

            if (batch is not null)
            {
                await WriteAsync(writer, batch, ct);
            }

            if (_clock.Elapsed - lastProgress >= TimeSpan.FromSeconds(10))
            {
                lastProgress = _clock.Elapsed;
                log.WriteLine(_counters.Progress(_clock.Elapsed));
            }

            if (hasNext && enumerator.Current.OffsetTicks > _clock.Elapsed.Ticks)
            {
                await timer.WaitForNextTickAsync(ct);
            }
        }
    }

    /// <summary>Returns true when the channel was full and the dispatcher had to wait.</summary>
    private async ValueTask<bool> WriteAsync(ChannelWriter<Batch> writer, Batch batch, CancellationToken ct)
    {
        if (writer.TryWrite(batch))
        {
            return false;
        }

        var blocked = Stopwatch.GetTimestamp();
        await writer.WriteAsync(batch, ct);
        _counters.RecordBackpressure(Stopwatch.GetElapsedTime(blocked));
        return true;
    }

    private async Task SendLoopGuardedAsync(ChannelReader<Batch> reader, DateTimeOffset runStart, CancellationTokenSource failed)
    {
        try
        {
            await SendLoopAsync(reader, runStart, failed.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _counters.RecordSenderFailure(ex);
            await failed.CancelAsync();
        }
    }

    private async Task SendLoopAsync(ChannelReader<Batch> reader, DateTimeOffset runStart, CancellationToken ct)
    {
        // One buffer per sender, reused for every request (the previous request has finished before the next starts).
        var buffer = new ArrayBufferWriter<byte>(64 * 1024);
        using var writer = EventJsonWriter.CreateWriter(buffer);

        await foreach (var batch in reader.ReadAllAsync(ct))
        {
            try
            {
                buffer.ResetWrittenCount();
                writer.Reset(buffer);
                EventJsonWriter.WriteBatch(writer, batch.Events, topology, runStart, options.TimeScale);
                _counters.RecordSent(batch.Events);
                await SendAsync(buffer.WrittenMemory, batch.Count, batch.FirstOffsetTicks, ct);
            }
            finally
            {
                batch.Return();
            }
        }
    }

    private async Task SendAsync(ReadOnlyMemory<byte> body, int count, long scheduledTicks, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            using var content = new ReadOnlyMemoryContent(body);
            content.Headers.ContentType = JsonContentType;
            using var response = await http.PostAsync(new Uri(IngestPath, UriKind.Relative), content, ct);
            _counters.RecordStatus(response.StatusCode, Stopwatch.GetElapsedTime(started), Scheduled(scheduledTicks));

            if (response.StatusCode == HttpStatusCode.Accepted)
            {
                try
                {
                    await using var stream = await response.Content.ReadAsStreamAsync(ct);
                    using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                    var root = json.RootElement;
                    _counters.RecordAccepted(
                        root.GetProperty("accepted").GetInt32(),
                        root.GetProperty("rejected").GetArrayLength(),
                        root.GetProperty("duplicatesInBatch").GetInt32());
                }
                catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
                {
                    _counters.RecordErrors(count, ex); // 202 with an unexpected body (review finding 4)
                }
            }
            else if (response.StatusCode == HttpStatusCode.BadRequest)
            {
                _counters.RecordAccepted(accepted: 0, rejected: count, duplicates: 0); // the whole batch was refused
            }
            else
            {
                _counters.RecordErrors(count, error: null); // 5xx, 429…: the API did not take the events
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            // No answer: connection refused or reset, or timeout (HttpClient.Timeout).
            _counters.RecordNoResponse(count, Stopwatch.GetElapsedTime(started), Scheduled(scheduledTicks), ex);
        }
    }

    /// <summary>Time from the scheduled (sensor) time of the batch's first event until now.</summary>
    private TimeSpan Scheduled(long scheduledTicks) => TimeSpan.FromTicks(_clock.Elapsed.Ticks - scheduledTicks);

    /// <summary>A pooled array of events (one per request; returned after sending).</summary>
    private sealed class Batch
    {
        private SimulatedEvent[] _items = [];

        public int Count { get; private set; }

        /// <summary>Sensor time of the oldest event of the batch (the earliest the batch could have been sent).</summary>
        public long FirstOffsetTicks { get; private set; }

        public ReadOnlySpan<SimulatedEvent> Events => _items.AsSpan(0, Count);

        public static Batch Rent(int capacity) => new() { _items = ArrayPool<SimulatedEvent>.Shared.Rent(capacity) };

        public void Add(in SimulatedEvent e)
        {
            if (Count == 0)
            {
                FirstOffsetTicks = e.OffsetTicks;
            }

            _items[Count++] = e;
        }

        public void Return()
        {
            ArrayPool<SimulatedEvent>.Shared.Return(_items);
            _items = [];
            Count = 0;
        }
    }
}
