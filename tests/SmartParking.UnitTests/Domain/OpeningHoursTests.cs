using SmartParking.Domain.Common;
using SmartParking.Domain.Facilities;

namespace SmartParking.UnitTests.Domain;

/// <summary>
/// Opening hours at an instant, in the facility's time zone: [opensAt, closesAt), and a day without a row is closed
/// (spec 002; decision 4 of plan 002a).
/// </summary>
public sealed class OpeningHoursTests
{
    private static readonly ParkingFacility Facility = ParkingFacility.Create(
        Guid.CreateVersion7(),
        Operator.ReferenceOperatorId,
        "Parque da Sofia",
        "Rua da Sofia",
        new GeoCoordinate(40.2033, -8.4103),
        "Europe/Lisbon",
        [new(DayOfWeek.Wednesday, new TimeOnly(8, 0), new TimeOnly(20, 0))]);

    [Theory]
    [InlineData("2026-12-02T07:59:59Z", false)] // Wednesday, Lisbon = UTC in winter
    [InlineData("2026-12-02T08:00:00Z", true)]
    [InlineData("2026-12-02T19:59:59Z", true)]
    [InlineData("2026-12-02T20:00:00Z", false)] // closing time is already closed
    [InlineData("2026-12-03T10:00:00Z", false)] // Thursday has no row
    [InlineData("2026-07-01T07:30:00Z", true)] // Wednesday 08:30 in Lisbon summer time (UTC+1)
    [InlineData("2026-07-01T19:00:00Z", false)] // Wednesday 20:00 in Lisbon summer time
    public void IsOpenAt_Instant_FollowsLocalOpeningHours(string instant, bool expected)
    {
        // Act
        var open = Facility.IsOpenAt(DateTimeOffset.Parse(instant, System.Globalization.CultureInfo.InvariantCulture));

        // Assert
        Assert.Equal(expected, open);
    }
}
