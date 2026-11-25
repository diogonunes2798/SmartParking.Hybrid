using SmartParking.Domain.Common;
using SmartParking.Domain.Facilities;
using SmartParking.Domain.Facilities.Events;

namespace SmartParking.UnitTests.Domain;

public sealed class ParkingFacilityTests
{
    private static readonly GeoCoordinate Coimbra = new(40.2033, -8.4103);

    [Fact]
    public void Create_ValidInput_IsActiveAndRaisesFacilityCreated()
    {
        // Act
        var facility = NewFacility();

        // Assert
        Assert.Equal(FacilityStatus.Active, facility.Status);
        var created = Assert.IsType<FacilityCreated>(Assert.Single(facility.DomainEvents));
        Assert.Equal(facility.Id, created.Id);
        Assert.Equal(Coimbra.Latitude, created.Latitude);
        Assert.Equal(Coimbra.Longitude, created.Longitude);
    }

    [Fact]
    public void Create_RepeatedDay_Throws()
    {
        // Arrange
        FacilityOpeningHours[] hours =
        [
            new(DayOfWeek.Monday, new TimeOnly(8, 0), new TimeOnly(12, 0)),
            new(DayOfWeek.Monday, new TimeOnly(14, 0), new TimeOnly(20, 0)),
        ];

        // Act + Assert
        Assert.Throws<ArgumentException>(() => NewFacility(hours));
    }

    [Theory]
    [InlineData("Mars/Phobos")]
    [InlineData("GMT Standard Time")]
    [InlineData("")]
    public void Create_InvalidTimeZone_Throws(string timeZone)
    {
        // Act + Assert
        Assert.Throws<ArgumentException>(() => ParkingFacility.Create(
            Guid.CreateVersion7(), Operator.ReferenceOperatorId, "P1", "Rua A", Coimbra, timeZone, []));
    }

    [Fact]
    public void Update_SameValues_ReturnsFalseWithoutEvent()
    {
        // Arrange
        var facility = NewFacility();
        facility.DequeueDomainEvents();

        // Act
        var changed = facility.Update(
            facility.Name, facility.Address, new GeoCoordinate(Coimbra.Latitude, Coimbra.Longitude),
            facility.TimeZone, facility.Status, [new(DayOfWeek.Monday, new TimeOnly(8, 0), new TimeOnly(20, 0))]);

        // Assert
        Assert.False(changed);
        Assert.Empty(facility.DomainEvents);
    }

    [Fact]
    public void Update_NewHoursAndStatus_MergesByDayAndRaisesFacilityUpdated()
    {
        // Arrange
        var facility = NewFacility();
        var monday = facility.OpeningHours[0];
        facility.DequeueDomainEvents();

        // Act
        var changed = facility.Update(
            facility.Name, facility.Address, Coimbra, facility.TimeZone, FacilityStatus.Inactive,
            [
                new(DayOfWeek.Monday, new TimeOnly(9, 0), new TimeOnly(18, 0)),
                new(DayOfWeek.Saturday, new TimeOnly(10, 0), new TimeOnly(13, 0)),
            ]);

        // Assert
        Assert.True(changed);
        Assert.Same(monday, facility.OpeningHours.Single(hours => hours.DayOfWeek == DayOfWeek.Monday));
        Assert.Equal(new TimeOnly(9, 0), monday.OpensAt);
        Assert.Equal(2, facility.OpeningHours.Count);
        var updated = Assert.IsType<FacilityUpdated>(Assert.Single(facility.DomainEvents));
        Assert.Equal(FacilityStatus.Inactive, updated.Status);
    }

    [Fact]
    public void MarkForDeletion_RaisesFacilityDeletedWithLastState()
    {
        // Arrange
        var facility = NewFacility();
        facility.DequeueDomainEvents();

        // Act
        facility.MarkForDeletion();

        // Assert
        var deleted = Assert.IsType<FacilityDeleted>(Assert.Single(facility.DomainEvents));
        Assert.Equal(facility.Name, deleted.Name);
    }

    [Fact]
    public void OpeningHours_OpensAtNotBeforeClosesAt_Throws()
    {
        // Act + Assert
        Assert.Throws<ArgumentException>(() => new FacilityOpeningHours(DayOfWeek.Friday, new TimeOnly(20, 0), new TimeOnly(8, 0)));
    }

    private static ParkingFacility NewFacility(IEnumerable<FacilityOpeningHours>? hours = null) =>
        ParkingFacility.Create(
            Guid.CreateVersion7(),
            Operator.ReferenceOperatorId,
            "Parque Central",
            "Rua da Sofia, Coimbra",
            Coimbra,
            "Europe/Lisbon",
            hours ?? [new(DayOfWeek.Monday, new TimeOnly(8, 0), new TimeOnly(20, 0))]);
}
