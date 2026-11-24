using SmartParking.Domain.Common;

namespace SmartParking.Domain.Facilities;

/// <summary>
/// Version of a facility's tariff, valid in [<see cref="ValidFrom"/>, <see cref="ValidTo"/>).
/// At most one version is valid at any instant per facility (EXCLUDE constraint).
/// The version rules (closing, deleting and reopening) arrive in F2b.
/// </summary>
public sealed class Tariff : AggregateRoot
{
    public const int MinBillingIncrementMinutes = 1;

    public const int MaxBillingIncrementMinutes = 60;

    public const string DefaultCurrency = "EUR";

    public const int CurrencyLength = 3;

    private Tariff()
    {
    }

    public Guid FacilityId { get; private init; }

    public DateTimeOffset ValidFrom { get; private init; }

    public DateTimeOffset? ValidTo { get; private set; }

    public decimal PricePerHour { get; private init; }

    public int BillingIncrementMinutes { get; private init; }

    public decimal? DailyMaximum { get; private init; }

    public int FreeMinutes { get; private init; }

    /// <summary>ISO 4217 code: three upper-case letters.</summary>
    public string Currency { get; private init; } = DefaultCurrency;

    public uint Version { get; private set; }

    public static bool IsValidCurrency(string? currency) =>
        currency is { Length: CurrencyLength } && currency.All(char.IsAsciiLetterUpper);

    public static Tariff Create(
        Guid id,
        Guid facilityId,
        DateTimeOffset validFrom,
        decimal pricePerHour,
        int billingIncrementMinutes,
        decimal? dailyMaximum,
        int freeMinutes,
        string currency = DefaultCurrency)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pricePerHour);
        ArgumentOutOfRangeException.ThrowIfLessThan(billingIncrementMinutes, MinBillingIncrementMinutes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(billingIncrementMinutes, MaxBillingIncrementMinutes);
        ArgumentOutOfRangeException.ThrowIfNegative(freeMinutes);

        if (dailyMaximum is { } maximum)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(maximum, nameof(dailyMaximum));
        }

        if (!IsValidCurrency(currency))
        {
            throw new ArgumentException("The currency must be three upper-case letters.", nameof(currency));
        }

        return new Tariff
        {
            Id = Guard.Id(id),
            FacilityId = Guard.Id(facilityId),
            ValidFrom = validFrom,
            PricePerHour = pricePerHour,
            BillingIncrementMinutes = billingIncrementMinutes,
            DailyMaximum = dailyMaximum,
            FreeMinutes = freeMinutes,
            Currency = currency,
        };
    }
}
