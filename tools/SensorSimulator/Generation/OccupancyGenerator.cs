using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using SmartParking.SensorSimulator.Provisioning;
using static System.FormattableString;

namespace SmartParking.SensorSimulator.Generation;

/// <summary>
/// Occupancy events of the reference scenario (spec 003, rounds 6 and 7), as a pure function of the mapping, the
/// settings and the start instant:
/// <list type="bullet">
/// <item>each spot is a Poisson process: exponential intervals with rate <c>1/mean</c>, or <c>factor/mean</c> inside a
/// peak window. The rate is constant between window edges, so a draw that crosses an edge is redrawn from the edge
/// (exact, because the exponential distribution has no memory);</item>
/// <item>each spot starts in a state drawn with the seed and then alternates, as a real sensor: the first event is the
/// opposite of the initial state;</item>
/// <item>each spot has its own <see cref="Random"/>, seeded from the seed and its <c>hardwareId</c>, so its sequence
/// does not depend on the other spots;</item>
/// <item>the <c>eventId</c> is a UUID version 8 from SHA-256 of the seed, the start instant, the <c>hardwareId</c> and
/// the spot's sequence number: the same seed and clock give the same ids (AC-20), and two runs started at different
/// instants do not collide in the idempotent event log.</item>
/// </list>
/// The sequence is lazy, infinite and ordered by timestamp; callers stop it with <c>TakeWhile</c>.
/// </summary>
internal static class OccupancyGenerator
{
    private static readonly TimeSpan OneMillisecond = TimeSpan.FromMilliseconds(1);

    public static IEnumerable<SimulatedEvent> Generate(IReadOnlyList<SensorMapEntry> sensors, GeneratorSettings settings, DateTimeOffset start)
    {
        ArgumentNullException.ThrowIfNull(sensors);
        ArgumentNullException.ThrowIfNull(settings);

        var origin = TruncateToMilliseconds(start.ToUniversalTime());
        return Enumerate(sensors, settings, origin);
    }

    /// <summary>The start instant as the generator uses it: UTC, whole milliseconds.</summary>
    public static DateTimeOffset TruncateToMilliseconds(DateTimeOffset value) =>
        new(value.UtcTicks - (value.UtcTicks % TimeSpan.TicksPerMillisecond), TimeSpan.Zero);

    private static IEnumerable<SimulatedEvent> Enumerate(IReadOnlyList<SensorMapEntry> sensors, GeneratorSettings settings, DateTimeOffset origin)
    {
        var queue = new PriorityQueue<SpotProcess, TimeSpan>(sensors.Count);
        foreach (var sensor in sensors)
        {
            var random = new Random(SpotSeed(settings.Seed, sensor.HardwareId));
            var process = new SpotProcess(sensor, random, occupied: random.Next(2) == 1);
            process.Next = NextOffset(TimeSpan.Zero, random, settings);
            queue.Enqueue(process, process.Next);
        }

        while (queue.TryDequeue(out var process, out var offset))
        {
            // Whole milliseconds on the wire; a spot's timestamps stay strictly increasing even if two draws fall in the same millisecond.
            var timestamp = TruncateToMilliseconds(origin + offset);
            if (process.LastTimestamp is { } last && timestamp <= last)
            {
                timestamp = last + OneMillisecond;
            }

            process.Occupied = !process.Occupied;
            process.LastTimestamp = timestamp;
            var sequence = process.Sequence++;

            yield return new SimulatedEvent(
                EventId(settings.Seed, origin, process.Sensor.HardwareId, sequence),
                process.Sensor.HardwareId,
                process.Sensor.SpotId,
                process.Sensor.FacilityId,
                process.Occupied ? SimulatedEventType.Occupied : SimulatedEventType.Freed,
                timestamp);

            process.Next = NextOffset(offset, process.Random, settings);
            queue.Enqueue(process, process.Next);
        }
    }

    /// <summary>The next event after <paramref name="from"/> of a Poisson process whose rate is constant between window edges.</summary>
    private static TimeSpan NextOffset(TimeSpan from, Random random, GeneratorSettings settings)
    {
        var current = from;
        while (true)
        {
            // 1 - U lies in (0, 1], so the logarithm is finite.
            var draw = -Math.Log(1.0 - random.NextDouble()) / settings.RateAt(current);
            var candidate = current + TimeSpan.FromSeconds(draw);
            if (settings.NextBoundaryAfter(current) is { } boundary && candidate >= boundary)
            {
                current = boundary;
                continue;
            }

            return candidate;
        }
    }

    // The hashed text is formatted with the invariant culture, so a negative seed gives the same spot seeds and eventIds
    // on every machine (review of F4b, finding 6).
    private static int SpotSeed(int seed, string hardwareId)
    {
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(Invariant($"{seed}|{hardwareId}")), hash);
        return BinaryPrimitives.ReadInt32BigEndian(hash);
    }

    private static Guid EventId(int seed, DateTimeOffset origin, string hardwareId, long sequence)
    {
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(Invariant($"{seed}|{origin.UtcTicks}|{hardwareId}|{sequence}")), hash);

        var bytes = hash[..16];
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x80); // version 8 (RFC 9562, custom)
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // variant 10
        return new Guid(bytes, bigEndian: true);
    }

    private sealed class SpotProcess(SensorMapEntry sensor, Random random, bool occupied)
    {
        public SensorMapEntry Sensor { get; } = sensor;

        public Random Random { get; } = random;

        public bool Occupied { get; set; } = occupied;

        public long Sequence { get; set; }

        public TimeSpan Next { get; set; }

        public DateTimeOffset? LastTimestamp { get; set; }
    }
}
