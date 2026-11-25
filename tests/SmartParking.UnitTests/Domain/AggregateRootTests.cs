using SmartParking.Domain.Facilities;
using SmartParking.Domain.Facilities.Events;

namespace SmartParking.UnitTests.Domain;

/// <summary>Raised events and the entities they changed (spec 002, ADR 0006).</summary>
public sealed class AggregateRootTests
{
    [Fact]
    public void Raise_WithoutChangedEntities_ChangesOnlyTheRoot()
    {
        // Arrange
        var zone = Zone.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), "Piso 0");

        // Act
        var raised = Assert.Single(zone.DequeueRaisedEvents());

        // Assert
        Assert.IsType<ZoneCreated>(raised.Event);
        Assert.Same(zone, Assert.Single(raised.Changed));
    }

    [Fact]
    public void DequeueRaisedEvents_SecondCall_ReturnsNothing()
    {
        // Arrange
        var zone = Zone.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), "Piso 0");
        zone.DequeueRaisedEvents();

        // Act
        var pending = zone.DequeueRaisedEvents();

        // Assert
        Assert.Empty(pending);
        Assert.Empty(zone.DomainEvents);
    }
}
