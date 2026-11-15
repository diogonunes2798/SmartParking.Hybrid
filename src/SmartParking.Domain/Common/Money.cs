namespace SmartParking.Domain.Common;

/// <summary>
/// Monetary amounts in EUR, stored as <c>numeric(10,2)</c>. Rounding and the upper bound live in the domain,
/// so PostgreSQL never rounds silently nor overflows (review of Phase 2a, finding 5).
/// </summary>
public static class Money
{
    /// <summary>Exclusive upper bound of <c>numeric(10,2)</c>: 10^8.</summary>
    public const decimal Limit = 100_000_000m;

    /// <summary>Rounds to cents (half away from zero) and checks the range [0, 10^8).</summary>
    public static decimal Normalise(decimal amount, string field)
    {
        var rounded = decimal.Round(amount, 2, MidpointRounding.AwayFromZero);
        if (rounded < 0)
        {
            throw new DomainValidationException(field, "must not be negative");
        }

        if (rounded >= Limit)
        {
            throw new DomainValidationException(field, $"must be lower than {Limit}");
        }

        return rounded;
    }
}
