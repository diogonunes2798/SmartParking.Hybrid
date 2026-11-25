using SmartParking.Domain.Common;
using SmartParking.Domain.Facilities.Events;

namespace SmartParking.Domain.Facilities;

/// <summary>
/// Version of a facility's tariff, valid in [<see cref="ValidFrom"/>, <see cref="ValidTo"/>) (ADR 0005).
/// At most one version is valid at any instant per facility (EXCLUDE constraint). Rules (spec 001):
/// <list type="bullet">
/// <item>a new version starts now or later, after the start of the latest one, and closes the latest one;</item>
/// <item>a version that has started is immutable;</item>
/// <item>only the latest version, and only while it is in the future, can be deleted; that reopens the previous one.</item>
/// </list>
/// Only the latest version is open (<see cref="ValidTo"/> is <c>null</c>).
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

    /// <summary>The latest version of its facility: the only one still open.</summary>
    public bool IsLatest => ValidTo is null;

    public static bool IsValidCurrency(string? currency) =>
        currency is { Length: CurrencyLength } && currency.All(char.IsAsciiLetterUpper);

    /// <summary>
    /// Creates the next version of a facility's tariff and closes <paramref name="latest"/> at <paramref name="validFrom"/>.
    /// The caller checks the rules first (400 in the past, 409 not after the latest); breaking them here is a programming error.
    /// </summary>
    public static Tariff CreateVersion(
        Guid id,
        Guid facilityId,
        Tariff? latest,
        DateTimeOffset validFrom,
        decimal pricePerHour,
        int billingIncrementMinutes,
        decimal? dailyMaximum,
        int freeMinutes,
        string currency,
        DateTimeOffset now)
    {
        var start = validFrom.ToUniversalTime();
        ArgumentOutOfRangeException.ThrowIfLessThan(start, now, nameof(validFrom));

        if (latest is not null)
        {
            if (latest.FacilityId != facilityId || !latest.IsLatest)
            {
                throw new ArgumentException("Only the latest version of the same facility can be closed.", nameof(latest));
            }

            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(start, latest.ValidFrom, nameof(validFrom));
        }

        var tariff = Create(id, facilityId, start, pricePerHour, billingIncrementMinutes, dailyMaximum, freeMinutes, currency);

        // Closing the latest version is part of this creation: one audit entry and one event (AC-7, ADR 0005).
        latest?.ValidTo = start;

        tariff.Raise(new TariffVersionCreated(
            tariff.Id, tariff.FacilityId, tariff.ValidFrom, tariff.PricePerHour, tariff.BillingIncrementMinutes,
            tariff.DailyMaximum, tariff.FreeMinutes, tariff.Currency, latest?.Id, latest?.ValidTo));
        return tariff;
    }

    /// <summary>Only the latest version, while it has not started yet, can be deleted (AC-9).</summary>
    public bool CanBeDeleted(DateTimeOffset now) => IsLatest && ValidFrom > now;

    /// <summary>
    /// Records the deletion and reopens <paramref name="previous"/>, the version this one closed (if any).
    /// The caller checks <see cref="CanBeDeleted"/> first (409). A version referenced by sessions is kept by the FK.
    /// </summary>
    public void MarkForDeletion(Tariff? previous, DateTimeOffset now)
    {
        if (!CanBeDeleted(now))
        {
            throw new InvalidOperationException("Only the latest version, before it starts, can be deleted.");
        }

        if (previous is not null)
        {
            if (previous.FacilityId != FacilityId || previous.ValidTo != ValidFrom)
            {
                throw new ArgumentException("The previous version is the one this version closed.", nameof(previous));
            }

            previous.ValidTo = null;
        }

        Raise(new TariffVersionDeleted(
            Id, FacilityId, ValidFrom, PricePerHour, BillingIncrementMinutes, DailyMaximum, FreeMinutes, Currency, previous?.Id));
    }

    private static Tariff Create(
        Guid id,
        Guid facilityId,
        DateTimeOffset validFrom,
        decimal pricePerHour,
        int billingIncrementMinutes,
        decimal? dailyMaximum,
        int freeMinutes,
        string currency)
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
