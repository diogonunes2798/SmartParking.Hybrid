using SmartParking.Domain.Facilities;
using SmartParking.Domain.Sessions;

namespace SmartParking.UnitTests.Domain;

/// <summary>AC-8 (spec 002): amount of a session, as a table of cases.</summary>
public sealed class SessionPricingTests
{
    /// <summary>price per hour, increment (min), free minutes, daily maximum (null = none), duration (min), expected amount.</summary>
    public static TheoryData<decimal, int, int, decimal?, int, decimal> Cases { get; } = new()
    {
        // Reference tariff of the spec: 1.20 €/h, 15 min, 15 free minutes, daily maximum 10.00 €.
        { 1.20m, 15, 15, 10.00m, 10, 0.00m },
        { 1.20m, 15, 15, 10.00m, 15, 0.00m },
        { 1.20m, 15, 15, 10.00m, 40, 0.90m },
        { 1.20m, 15, 15, 10.00m, 31 * 60, 18.40m },
        { 1.20m, 15, 15, 10.00m, 48 * 60, 20.00m },

        // Half a cent rounds up, once, at the end: 1 min at 0.30 €/h is 0.005 €.
        { 0.30m, 1, 0, null, 1, 0.01m },

        // Without a daily maximum every block is paid: 31 h = 124 blocks × 0.30 €.
        { 1.20m, 15, 15, null, 31 * 60, 37.20m },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Calculate_ReferenceTariff_ReturnsExpected_AC8(
        decimal pricePerHour,
        int incrementMinutes,
        int freeMinutes,
        decimal? dailyMaximum,
        int durationMinutes,
        decimal expected)
    {
        // Arrange
        var tariff = NewTariff(pricePerHour, incrementMinutes, freeMinutes, dailyMaximum);

        // Act
        var amount = SessionPricing.Calculate(tariff, TimeSpan.FromMinutes(durationMinutes));

        // Assert
        Assert.Equal(expected, amount);
    }

    [Fact]
    public void Calculate_IncrementNotDividingADay_RoundsBlocksUpPerPeriod()
    {
        // Arrange: 7 min blocks of 0.70 €. 24 h = 205.7 blocks → 206; the minute after starts a new period → 1 more.
        var tariff = NewTariff(6.00m, 7, 0, null);

        // Act
        var amount = SessionPricing.Calculate(tariff, TimeSpan.FromMinutes((24 * 60) + 1));

        // Assert
        Assert.Equal(207 * 0.70m, amount);
    }

    [Fact]
    public void Calculate_JustAboveFreeMinutes_PaysTheWholeDuration()
    {
        // Arrange
        var tariff = NewTariff(1.20m, 15, 15, 10.00m);

        // Act
        var amount = SessionPricing.Calculate(tariff, TimeSpan.FromMinutes(15).Add(TimeSpan.FromMicroseconds(1)));

        // Assert
        Assert.Equal(0.60m, amount);
    }

    private static Tariff NewTariff(decimal pricePerHour, int incrementMinutes, int freeMinutes, decimal? dailyMaximum)
    {
        var now = new DateTimeOffset(2026, 12, 1, 9, 0, 0, TimeSpan.Zero);
        return Tariff.CreateVersion(
            Guid.CreateVersion7(), Guid.CreateVersion7(), null, now, pricePerHour, incrementMinutes, dailyMaximum, freeMinutes, "EUR", now);
    }
}
