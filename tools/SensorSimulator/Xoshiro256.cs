using System.Numerics;

namespace SmartParking.SensorSimulator;

/// <summary>
/// xoshiro256** pseudo-random generator (Blackman and Vigna), seeded with SplitMix64. It is used instead of
/// <see cref="Random"/> because the .NET documentation does not guarantee that a seeded <see cref="Random"/> gives
/// the same sequence across runtime versions; with this algorithm a scenario and its seed define the event sequence
/// for good (reproducibility, CLAUDE.md). Not cryptographic, and not thread-safe: one instance per stream.
/// </summary>
internal sealed class Xoshiro256
{
    private ulong _s0;
    private ulong _s1;
    private ulong _s2;
    private ulong _s3;

    /// <param name="seed">Scenario seed.</param>
    /// <param name="stream">Independent stream of the same seed (arrivals, sensor thresholds, readings, ids).</param>
    public Xoshiro256(long seed, ulong stream)
    {
        var x = unchecked((ulong)seed) ^ (stream * 0x9E3779B97F4A7C15UL);
        _s0 = SplitMix64(ref x);
        _s1 = SplitMix64(ref x);
        _s2 = SplitMix64(ref x);
        _s3 = SplitMix64(ref x);
    }

    public ulong NextUInt64()
    {
        var result = BitOperations.RotateLeft(_s1 * 5, 7) * 9;
        var t = _s1 << 17;
        _s2 ^= _s0;
        _s3 ^= _s1;
        _s1 ^= _s2;
        _s0 ^= _s3;
        _s2 ^= t;
        _s3 = BitOperations.RotateLeft(_s3, 45);
        return result;
    }

    /// <summary>Uniform double in [0, 1) with 53 random bits.</summary>
    public double NextDouble() => (NextUInt64() >> 11) * (1.0 / (1UL << 53));

    /// <summary>Uniform integer in [0, <paramref name="maxExclusive"/>), without modulo bias (Lemire).</summary>
    public int NextInt(int maxExclusive)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxExclusive);
        var bound = (ulong)maxExclusive;
        var product = Math.BigMul(NextUInt64(), bound, out var low);
        if (low < bound)
        {
            var threshold = (0 - bound) % bound;
            while (low < threshold)
            {
                product = Math.BigMul(NextUInt64(), bound, out low);
            }
        }

        return (int)product;
    }

    /// <summary>A random version 4 GUID (RFC 9562) built from this stream.</summary>
    public Guid NextGuid()
    {
        Span<byte> bytes = stackalloc byte[16];
        BitConverter.TryWriteBytes(bytes, NextUInt64());
        BitConverter.TryWriteBytes(bytes[8..], NextUInt64());
        bytes[7] = (byte)((bytes[7] & 0x0F) | 0x40); // version 4 (Guid's little-endian field layout)
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // RFC 9562 variant
        return new Guid(bytes);
    }

    private static ulong SplitMix64(ref ulong x)
    {
        x += 0x9E3779B97F4A7C15UL;
        var z = x;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
}
