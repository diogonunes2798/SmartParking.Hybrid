namespace SmartParking.Domain.Common;

/// <summary>Small invariant helpers; every failure is a <see cref="DomainValidationException"/>.</summary>
internal static class Guard
{
    public static string Text(string? value, string field, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new DomainValidationException(field, "is required");
        }

        var trimmed = value.Trim();

        // PostgreSQL text cannot hold NUL, and control characters have no place in names or codes:
        // reject them here instead of letting the database fail with a 500 (review of Phase 2a, finding 2).
        if (trimmed.Any(char.IsControl))
        {
            throw new DomainValidationException(field, "must not contain control characters");
        }

        if (trimmed.Length > maxLength)
        {
            throw new DomainValidationException(field, $"must have at most {maxLength} characters");
        }

        return trimmed;
    }

    public static Guid Id(Guid value, string field) =>
        value == Guid.Empty ? throw new DomainValidationException(field, "is required") : value;

    public static T Defined<T>(T value, string field)
        where T : struct, Enum =>
        Enum.IsDefined(value) ? value : throw new DomainValidationException(field, $"'{value}' is not a valid value");

    /// <summary>
    /// Normalises an instant to what PostgreSQL <c>timestamptz</c> stores: UTC (required by Npgsql) and
    /// microsecond precision (.NET ticks are 100 ns). Without the truncation, two instants that differ below
    /// 1 µs pass domain checks but collide in the database, and the value returned after a write differs from
    /// the one read back (review of Phase 2a, finding 2).
    /// </summary>
    public static DateTimeOffset Utc(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return utc.AddTicks(-(utc.Ticks % TicksPerMicrosecond));
    }

    private const long TicksPerMicrosecond = TimeSpan.TicksPerMillisecond / 1000;
}
