namespace SmartParking.Domain.Tariffs;

/// <summary>
/// Computes the amount due of a parking period under a tariff. Extension point: the rule model of the
/// tariff is still provisional (TODO(Diogo), ADR-0003), so the calculation is behind an interface and the
/// UC-02 handlers do not change when it is replaced.
/// </summary>
public interface ITariffPricing
{
    /// <returns>Amount in EUR, rounded to cents.</returns>
    decimal Price(TariffRules rules, DateTimeOffset startedAt, DateTimeOffset endedAt);
}

/// <summary>
/// Provisional calculation over the provisional <see cref="TariffRules"/> (hourly rate, free minutes,
/// daily cap). TODO(Diogo): these rules are not in the specification (§3 only says «regras de cálculo»);
/// replace them with the tariff model of Chapter 3 when it is defined.
/// <list type="number">
/// <item>Duration in started minutes (a started minute is charged: 61 s → 2 min).</item>
/// <item>The first <see cref="TariffRules.FreeMinutes"/> minutes are free (once per session).</item>
/// <item>Billable minutes are charged at <see cref="TariffRules.HourlyRate"/> per hour, pro rata per minute.</item>
/// <item>If <see cref="TariffRules.DailyCap"/> is set, each block of 24 h of billable time costs at most the cap.</item>
/// <item>The result is rounded to cents, half away from zero.</item>
/// </list>
/// </summary>
public sealed class ProvisionalTariffPricing : ITariffPricing
{
    private const int MinutesPerDay = 24 * 60;

    public decimal Price(TariffRules rules, DateTimeOffset startedAt, DateTimeOffset endedAt)
    {
        ArgumentNullException.ThrowIfNull(rules);
        if (endedAt < startedAt)
        {
            throw new ArgumentOutOfRangeException(nameof(endedAt), "must not be earlier than startedAt");
        }

        var startedMinutes = (long)Math.Ceiling((endedAt - startedAt).TotalMinutes);
        var billable = Math.Max(0, startedMinutes - rules.FreeMinutes);

        decimal cost;
        if (rules.DailyCap is { } cap)
        {
            var fullDays = billable / MinutesPerDay;
            var rest = billable % MinutesPerDay;
            cost = (fullDays * Math.Min(cap, PerMinutes(rules.HourlyRate, MinutesPerDay)))
                + Math.Min(cap, PerMinutes(rules.HourlyRate, rest));
        }
        else
        {
            cost = PerMinutes(rules.HourlyRate, billable);
        }

        return decimal.Round(cost, 2, MidpointRounding.AwayFromZero);
    }

    private static decimal PerMinutes(decimal hourlyRate, long minutes) => hourlyRate * minutes / 60m;
}
