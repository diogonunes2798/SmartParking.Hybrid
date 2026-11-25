using SmartParking.Domain.Facilities;
using SmartParking.Domain.Facilities.Events;

namespace SmartParking.UnitTests.Domain;

/// <summary>Rules of the tariff versions (spec 001, ADR 0005).</summary>
public sealed class TariffVersionTests
{
    private static readonly Guid FacilityId = Guid.CreateVersion7();

    private static readonly DateTimeOffset Now = new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CreateVersion_WithLatest_ClosesLatestAndRaisesOneEventWithPrevious()
    {
        // Arrange
        var latest = NewVersion(latest: null, Now);
        latest.DequeueDomainEvents();
        var validFrom = Now.AddDays(1);

        // Act
        var next = NewVersion(latest, validFrom);

        // Assert
        Assert.Equal(validFrom, latest.ValidTo);
        Assert.False(latest.IsLatest);
        Assert.True(next.IsLatest);
        Assert.Empty(latest.DomainEvents);
        var created = Assert.IsType<TariffVersionCreated>(Assert.Single(next.DomainEvents));
        Assert.Equal(latest.Id, created.PreviousVersionId);
        Assert.Equal(validFrom, created.PreviousValidTo);
    }

    [Fact]
    public void CreateVersion_ValidFromWithOffset_IsStoredInUtc()
    {
        // Act
        var version = NewVersion(latest: null, new DateTimeOffset(2026, 11, 26, 10, 0, 0, TimeSpan.FromHours(2)));

        // Assert
        Assert.Equal(TimeSpan.Zero, version.ValidFrom.Offset);
        Assert.Equal(8, version.ValidFrom.Hour);
    }

    [Fact]
    public void CreateVersion_SubMicrosecondStart_ClosesLatestAtTheStoredInstant()
    {
        // Arrange
        var latest = NewVersion(latest: null, Now);
        var validFrom = Now.AddDays(1).AddTicks(7);

        // Act
        var next = NewVersion(latest, validFrom);

        // Assert
        Assert.Equal(0, next.ValidFrom.Ticks % 10);
        Assert.Equal(next.ValidFrom, latest.ValidTo);
    }

    [Fact]
    public void CreateVersion_InThePast_Throws()
    {
        // Act + Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => NewVersion(latest: null, Now.AddSeconds(-1)));
    }

    [Fact]
    public void CreateVersion_NotAfterLatest_Throws()
    {
        // Arrange
        var latest = NewVersion(latest: null, Now.AddDays(2));

        // Act + Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => NewVersion(latest, Now.AddDays(2)));
    }

    [Fact]
    public void CanBeDeleted_StartedVersion_IsFalse()
    {
        // Arrange
        var current = NewVersion(latest: null, Now);

        // Act + Assert
        Assert.False(current.CanBeDeleted(Now));
    }

    [Fact]
    public void CanBeDeleted_FutureVersionNotLatest_IsFalse()
    {
        // Arrange
        var future = NewVersion(latest: null, Now.AddDays(1));
        _ = NewVersion(future, Now.AddDays(2));

        // Act + Assert
        Assert.False(future.CanBeDeleted(Now));
    }

    [Fact]
    public void MarkForDeletion_LatestFutureVersion_ReopensPreviousAndRaisesOneEvent()
    {
        // Arrange
        var current = NewVersion(latest: null, Now);
        var future = NewVersion(current, Now.AddDays(1));
        current.DequeueDomainEvents();
        future.DequeueDomainEvents();

        // Act
        future.MarkForDeletion(current, Now);

        // Assert
        Assert.Null(current.ValidTo);
        Assert.Empty(current.DomainEvents);
        var deleted = Assert.IsType<TariffVersionDeleted>(Assert.Single(future.DomainEvents));
        Assert.Equal(current.Id, deleted.ReopenedVersionId);
    }

    [Fact]
    public void MarkForDeletion_StartedVersion_Throws()
    {
        // Arrange
        var current = NewVersion(latest: null, Now);

        // Act + Assert
        Assert.Throws<InvalidOperationException>(() => current.MarkForDeletion(previous: null, Now.AddMinutes(1)));
    }

    private static Tariff NewVersion(Tariff? latest, DateTimeOffset validFrom) =>
        Tariff.CreateVersion(
            Guid.CreateVersion7(), FacilityId, latest, validFrom, 1.20m, 15, dailyMaximum: 12m, freeMinutes: 10, "EUR", Now);
}
