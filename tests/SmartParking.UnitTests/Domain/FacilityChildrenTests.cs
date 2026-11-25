using SmartParking.Domain.Facilities;
using SmartParking.Domain.Facilities.Events;

namespace SmartParking.UnitTests.Domain;

/// <summary>Behaviour and events of zones, spots and sensors (spec 001, F2b).</summary>
public sealed class FacilityChildrenTests
{
    private static readonly Guid FacilityId = Guid.CreateVersion7();

    [Fact]
    public void Zone_Create_RaisesZoneCreated()
    {
        // Act
        var zone = Zone.Create(Guid.CreateVersion7(), FacilityId, " Piso 0 ");

        // Assert
        var created = Assert.IsType<ZoneCreated>(Assert.Single(zone.DomainEvents));
        Assert.Equal("Piso 0", created.Name);
        Assert.Equal(FacilityId, created.FacilityId);
    }

    [Fact]
    public void Zone_RenameSameName_ReturnsFalseWithoutEvent()
    {
        // Arrange
        var zone = Zone.Create(Guid.CreateVersion7(), FacilityId, "Piso 0");
        zone.DequeueDomainEvents();

        // Act
        var changed = zone.Rename("Piso 0");

        // Assert
        Assert.False(changed);
        Assert.Empty(zone.DomainEvents);
    }

    [Fact]
    public void Spot_UpdateZone_RaisesSpotUpdatedWithPreviousZone()
    {
        // Arrange
        var z1 = Guid.CreateVersion7();
        var z2 = Guid.CreateVersion7();
        var spot = ParkingSpot.Create(Guid.CreateVersion7(), FacilityId, z1, "A-001", SpotType.Standard);
        spot.DequeueDomainEvents();

        // Act
        var changed = spot.Update(z2, "A-001", SpotType.Standard, SpotStatus.Active);

        // Assert
        Assert.True(changed);
        var updated = Assert.IsType<SpotUpdated>(Assert.Single(spot.DomainEvents));
        Assert.Equal(z1, updated.PreviousZoneId);
        Assert.Equal(z2, updated.ZoneId);
    }

    [Fact]
    public void Spot_UpdateSameValues_ReturnsFalseWithoutEvent()
    {
        // Arrange
        var spot = ParkingSpot.Create(Guid.CreateVersion7(), FacilityId, Guid.CreateVersion7(), "A-001", SpotType.Accessible);
        spot.DequeueDomainEvents();

        // Act
        var changed = spot.Update(spot.ZoneId, "A-001", SpotType.Accessible, SpotStatus.Active);

        // Assert
        Assert.False(changed);
        Assert.Empty(spot.DomainEvents);
    }

    [Fact]
    public void Sensor_AssignToOtherSpot_RaisesSensorReassignedWithPreviousSpot()
    {
        // Arrange
        var l1 = Guid.CreateVersion7();
        var l2 = Guid.CreateVersion7();
        var sensor = NewOccupancySensor(l1);

        // Act
        var changed = sensor.AssignTo(l2);

        // Assert
        Assert.True(changed);
        var reassigned = Assert.IsType<SensorReassigned>(Assert.Single(sensor.DomainEvents));
        Assert.Equal(l1, reassigned.PreviousSpotId);
        Assert.Equal(l2, sensor.SpotId);
    }

    [Fact]
    public void Sensor_AssignToSameSpot_ReturnsFalseWithoutEvent()
    {
        // Arrange
        var l1 = Guid.CreateVersion7();
        var sensor = NewOccupancySensor(l1);

        // Act
        var changed = sensor.AssignTo(l1);

        // Assert
        Assert.False(changed);
        Assert.Empty(sensor.DomainEvents);
    }

    [Fact]
    public void Sensor_EnvironmentalAssignTo_Throws()
    {
        // Arrange
        var sensor = Sensor.Create(
            Guid.CreateVersion7(), FacilityId, "ENV-1", SensorKind.Environmental, "EnvSense", DateTimeOffset.UnixEpoch, null, null);

        // Act + Assert
        Assert.Throws<InvalidOperationException>(() => sensor.AssignTo(Guid.CreateVersion7()));
    }

    [Fact]
    public void Sensor_Unassign_ClearsSpotAndRaisesSensorUnassigned()
    {
        // Arrange
        var l1 = Guid.CreateVersion7();
        var sensor = NewOccupancySensor(l1);

        // Act
        var changed = sensor.Unassign();

        // Assert
        Assert.True(changed);
        Assert.Null(sensor.SpotId);
        Assert.Equal(l1, Assert.IsType<SensorUnassigned>(Assert.Single(sensor.DomainEvents)).PreviousSpotId);
    }

    [Fact]
    public void Sensor_UnassignWithoutSpot_ReturnsFalseWithoutEvent()
    {
        // Arrange
        var sensor = NewOccupancySensor(spotId: null);

        // Act
        var changed = sensor.Unassign();

        // Assert
        Assert.False(changed);
        Assert.Empty(sensor.DomainEvents);
    }

    [Fact]
    public void Sensor_RemoveWhileOnSpot_Throws()
    {
        // Arrange
        var sensor = NewOccupancySensor(Guid.CreateVersion7());

        // Act + Assert
        Assert.Throws<InvalidOperationException>(sensor.MarkForRemoval);
    }

    [Fact]
    public void Sensor_InstalledAtWithOffset_IsStoredInUtc()
    {
        // Act
        var sensor = Sensor.Create(
            Guid.CreateVersion7(), FacilityId, "HW-UTC", SensorKind.Occupancy, "OccuSense",
            new DateTimeOffset(2026, 11, 25, 10, 0, 0, TimeSpan.FromHours(1)), null, null);

        // Assert
        Assert.Equal(TimeSpan.Zero, sensor.InstalledAt.Offset);
        Assert.Equal(9, sensor.InstalledAt.Hour);
    }

    [Fact]
    public void Sensor_InstalledAtWithSubMicroseconds_IsTruncatedLikeTheDatabase()
    {
        // Act
        var sensor = Sensor.Create(
            Guid.CreateVersion7(), FacilityId, "HW-US", SensorKind.Occupancy, "OccuSense",
            DateTimeOffset.UnixEpoch.AddTicks(17), null, null);

        // Assert
        Assert.Equal(DateTimeOffset.UnixEpoch.AddTicks(10), sensor.InstalledAt);
        Assert.Equal(sensor.InstalledAt, Assert.IsType<SensorRegistered>(Assert.Single(sensor.DomainEvents)).InstalledAt);
    }

    private static Sensor NewOccupancySensor(Guid? spotId)
    {
        var sensor = Sensor.Create(
            Guid.CreateVersion7(), FacilityId, $"HW-{Guid.NewGuid():N}", SensorKind.Occupancy, "OccuSense",
            DateTimeOffset.UnixEpoch, spotId, zoneId: null);
        sensor.DequeueDomainEvents();
        return sensor;
    }
}
