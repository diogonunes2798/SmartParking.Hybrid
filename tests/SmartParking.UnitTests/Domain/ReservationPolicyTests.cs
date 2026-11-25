using SmartParking.Domain.Sessions;

namespace SmartParking.UnitTests.Domain;

/// <summary>Fulfilment window and expiry of a reservation (spec 002; plan 002b, proposal 4).</summary>
public sealed class ReservationPolicyTests
{
    private static readonly DateTimeOffset StartsAt = new(2026, 12, 2, 11, 0, 0, TimeSpan.Zero);

    private static readonly ReservationPolicy Policy = new(
        TimeSpan.FromHours(24), TimeSpan.FromDays(7), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(15));

    [Theory]
    [InlineData(-15 * 60 * 1_000_000L - 1, false)] // 1 µs before the early-arrival margin
    [InlineData(-15 * 60 * 1_000_000L, true)] // exactly at the margin: closed at the start
    [InlineData(0, true)]
    [InlineData(15 * 60 * 1_000_000L, true)] // exactly at the end of the grace: closed at the end
    [InlineData(15 * 60 * 1_000_000L + 1, false)]
    public void FulfilmentWindow_IsClosedAtBothEnds(long microsecondsFromStart, bool expected)
    {
        // Act
        var contains = Policy.FulfilmentWindow(StartsAt).Contains(StartsAt.AddMicroseconds(microsecondsFromStart));

        // Assert
        Assert.Equal(expected, contains);
    }

    [Theory]
    [InlineData(15 * 60 * 1_000_000L, false)]
    [InlineData(15 * 60 * 1_000_000L + 1, true)]
    public void IsDue_StrictlyAfterTheEndOfTheWindow(long microsecondsFromStart, bool expected)
    {
        // Act
        var due = Policy.IsDue(StartsAt, StartsAt.AddMicroseconds(microsecondsFromStart));

        // Assert
        Assert.Equal(expected, due);
    }

    [Fact]
    public void Constructor_NonPositiveMaxDurationOrNegativeGrace_Throws()
    {
        // Act + Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReservationPolicy(
            TimeSpan.Zero, TimeSpan.FromDays(7), TimeSpan.Zero, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReservationPolicy(
            TimeSpan.FromHours(1), TimeSpan.FromDays(7), TimeSpan.Zero, TimeSpan.FromMinutes(-1)));
    }
}
