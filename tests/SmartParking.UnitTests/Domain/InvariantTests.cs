using SmartParking.Domain.Common;
using SmartParking.Domain.Facilities;
using SmartParking.Domain.Sessions;
using SmartParking.Domain.Users;

namespace SmartParking.UnitTests.Domain;

/// <summary>Construction invariants of the value objects and of the entities without behaviour in F2a.</summary>
public sealed class InvariantTests
{
    [Theory]
    [InlineData(-90.0001, 0)]
    [InlineData(90.0001, 0)]
    [InlineData(0, -180.0001)]
    [InlineData(0, 180.0001)]
    public void GeoCoordinate_OutOfRange_Throws(double latitude, double longitude)
    {
        // Act + Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => new GeoCoordinate(latitude, longitude));
    }

    [Theory]
    [InlineData("Europe/Lisbon", true)]
    [InlineData("Atlantic/Azores", true)]
    [InlineData("GMT Standard Time", false)]
    [InlineData("Europe/Atlantis", false)]
    [InlineData(" ", false)]
    public void IanaTimeZone_IsValid_AcceptsOnlyIanaIds(string id, bool expected)
    {
        // Act + Assert
        Assert.Equal(expected, IanaTimeZone.IsValid(id));
    }

    [Fact]
    public void User_MixedCaseEmail_IsStoredInLowerCase()
    {
        // Act
        var user = User.Register(Guid.CreateVersion7(), "Ana", "  Ana.Silva@Example.PT ");

        // Assert
        Assert.Equal("ana.silva@example.pt", user.Email);
    }

    [Theory]
    [InlineData("aa-12 bb", "AA12BB")]
    [InlineData("12-AB-34", "12AB34")]
    public void Vehicle_LicensePlate_IsNormalised(string input, string expected)
    {
        // Act
        var vehicle = User.Register(Guid.CreateVersion7(), "Ana", "ana@example.pt").RegisterVehicle(Guid.CreateVersion7(), input);

        // Assert
        Assert.Equal(expected, vehicle.LicensePlate);
    }

    [Fact]
    public void Vehicle_LicensePlateWithSymbols_Throws()
    {
        // Arrange
        var user = User.Register(Guid.CreateVersion7(), "Ana", "ana@example.pt");

        // Act + Assert
        Assert.Throws<ArgumentException>(() => user.RegisterVehicle(Guid.CreateVersion7(), "AA/12"));
    }

    [Fact]
    public void Sensor_EnvironmentalOnSpot_Throws()
    {
        // Act + Assert
        Assert.Throws<ArgumentException>(() => Sensor.Create(
            Guid.CreateVersion7(), Guid.CreateVersion7(), "HW-1", SensorKind.Environmental, "EnvSense",
            DateTimeOffset.UnixEpoch, spotId: Guid.CreateVersion7(), zoneId: null));
    }

    [Theory]
    [InlineData(-0.01, 15, "EUR")]
    [InlineData(1.5, 0, "EUR")]
    [InlineData(1.5, 61, "EUR")]
    [InlineData(1.5, 15, "eur")]
    public void Tariff_InvalidValues_Throw(double pricePerHour, int billingIncrementMinutes, string currency)
    {
        // Act + Assert
        Assert.ThrowsAny<ArgumentException>(() => Tariff.CreateVersion(
            Guid.CreateVersion7(), Guid.CreateVersion7(), latest: null, DateTimeOffset.UnixEpoch, (decimal)pricePerHour,
            billingIncrementMinutes, dailyMaximum: null, freeMinutes: 0, currency, now: DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void Reservation_EndsBeforeItStarts_Throws()
    {
        // Arrange
        var startsAt = DateTimeOffset.UnixEpoch.AddHours(2);

        // Act + Assert
        Assert.Throws<ArgumentException>(() => Reservation.Reserve(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), startsAt, startsAt));
    }
}
