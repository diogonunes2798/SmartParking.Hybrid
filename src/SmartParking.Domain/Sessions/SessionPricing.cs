using SmartParking.Domain.Facilities;

namespace SmartParking.Domain.Sessions;

/// <summary>
/// Amount of a session (spec 002, «Cálculo do valor»):
/// <list type="number">
/// <item>free minutes are a threshold: up to <see cref="Tariff.FreeMinutes"/> the amount is 0; above it the whole
/// duration is paid;</item>
/// <item>the duration is split into 24 h periods from the start (the last one may be partial); each period has
/// ceil(minutes / increment) blocks, with or without a daily maximum (decision 3 of plan 002a);</item>
/// <item>a block costs increment / 60 × price per hour; each period pays at most <see cref="Tariff.DailyMaximum"/>,
/// when there is one;</item>
/// <item>the sum is rounded once, to 2 decimals, half away from zero.</item>
/// </list>
/// </summary>
public static class SessionPricing
{
    private static readonly TimeSpan Period = TimeSpan.FromHours(24);

    public static decimal Calculate(Tariff tariff, TimeSpan duration)
    {
        ArgumentNullException.ThrowIfNull(tariff);
        ArgumentOutOfRangeException.ThrowIfLessThan(duration, TimeSpan.Zero);

        if (duration <= TimeSpan.FromMinutes(tariff.FreeMinutes))
        {
            return 0m;
        }

        var incrementTicks = TimeSpan.FromMinutes(tariff.BillingIncrementMinutes).Ticks;
        var total = 0m;
        for (var remaining = duration; remaining > TimeSpan.Zero; remaining -= Period)
        {
            var periodLength = remaining < Period ? remaining : Period;

            // Integer ceiling on ticks: no floating point between the instant and the number of blocks.
            var blocks = (periodLength.Ticks + incrementTicks - 1) / incrementTicks;

            // Multiply before dividing, so a block price such as 0.30 / 60 stays exact in decimal.
            var periodAmount = blocks * tariff.BillingIncrementMinutes * tariff.PricePerHour / 60m;
            total += tariff.DailyMaximum is { } maximum ? Math.Min(periodAmount, maximum) : periodAmount;
        }

        return Math.Round(total, 2, MidpointRounding.AwayFromZero);
    }
}
