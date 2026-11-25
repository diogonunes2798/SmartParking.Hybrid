using System.Reflection;
using SmartParking.Domain.Sessions;
using SmartParking.Domain.Sessions.Events;

namespace SmartParking.UnitTests.Domain;

/// <summary>Lifecycle of a reservation, Active → Cancelled | Fulfilled | Expired (spec 002, UC-02, RF-02).</summary>
public sealed class ReservationTests
{
    private static readonly DateTimeOffset Start = new(2026, 12, 2, 11, 0, 0, TimeSpan.Zero);

    private static readonly ReservationPolicy Policy = new(
        TimeSpan.FromHours(24), TimeSpan.FromDays(7), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(15));

    [Fact]
    public void Reserve_NormalisesInstantsAndRaisesReservationCreated()
    {
        // Arrange
        var startsAt = new DateTimeOffset(2026, 12, 2, 12, 0, 0, TimeSpan.FromHours(1)).AddTicks(7);

        // Act
        var reservation = Reservation.Reserve(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), startsAt, startsAt.AddHours(1));

        // Assert
        Assert.Equal(ReservationStatus.Active, reservation.Status);
        Assert.Equal(TimeSpan.Zero, reservation.StartsAt.Offset);
        Assert.Equal(Start, reservation.StartsAt);
        Assert.Equal(Start.AddHours(1), reservation.EndsAt);
        var raised = Assert.Single(reservation.DequeueRaisedEvents());
        var created = Assert.IsType<ReservationCreated>(raised.Event);
        Assert.Equal(reservation.StartsAt, created.StartsAt);
        Assert.Same(reservation, Assert.Single(raised.Changed));
    }

    [Fact]
    public void Cancel_Active_CancelsAndRaisesReservationCancelled()
    {
        // Arrange
        var reservation = NewReservation();

        // Act
        reservation.Cancel();

        // Assert
        Assert.Equal(ReservationStatus.Cancelled, reservation.Status);
        Assert.IsType<ReservationCancelled>(Assert.Single(reservation.DomainEvents));
    }

    [Fact]
    public void Expire_AfterGrace_ExpiresAndRaisesReservationExpired()
    {
        // Arrange
        var reservation = NewReservation();

        // Act
        reservation.Expire(Start.AddMinutes(15).AddTicks(10), Policy);

        // Assert
        Assert.Equal(ReservationStatus.Expired, reservation.Status);
        Assert.IsType<ReservationExpired>(Assert.Single(reservation.DomainEvents));
    }

    [Fact]
    public void Expire_AtTheEndOfTheWindow_Throws()
    {
        // Arrange
        var reservation = NewReservation();

        // Act + Assert
        Assert.Throws<InvalidOperationException>(() => reservation.Expire(Start.AddMinutes(15), Policy));
        Assert.Equal(ReservationStatus.Active, reservation.Status);
    }

    [Theory]
    [InlineData("Cancel")]
    [InlineData("Expire")]
    [InlineData("Fulfil")]
    public void Transition_FromAFinalStatus_Throws(string transition)
    {
        // Arrange
        var reservation = NewReservation();
        reservation.Cancel();
        reservation.DequeueRaisedEvents();

        // Act
        Action act = transition switch
        {
            "Cancel" => reservation.Cancel,
            "Expire" => () => reservation.Expire(Start.AddHours(1), Policy),
            _ => () => ParkingSession.Open(Guid.CreateVersion7(), reservation.SpotId, reservation.VehicleId, Guid.CreateVersion7(), reservation, Start, Policy),
        };

        // Assert
        Assert.Throws<InvalidOperationException>(act);
        Assert.Equal(ReservationStatus.Cancelled, reservation.Status);
        Assert.Empty(reservation.DomainEvents);
    }

    [Fact]
    public void Fulfil_InWindow_FulfilsWithoutRaisingAnEvent()
    {
        // Arrange
        var reservation = NewReservation();

        // Act
        ParkingSession.Open(
            Guid.CreateVersion7(), reservation.SpotId, reservation.VehicleId, Guid.CreateVersion7(), reservation, Start.AddMinutes(-15), Policy);

        // Assert
        Assert.Equal(ReservationStatus.Fulfilled, reservation.Status);
        Assert.Empty(reservation.DomainEvents);
    }

    [Fact]
    public void CanBeFulfilledBy_OtherSpotOrVehicleOrOutsideWindow_IsFalse()
    {
        // Arrange
        var reservation = NewReservation();

        // Act + Assert
        Assert.True(reservation.CanBeFulfilledBy(reservation.SpotId, reservation.VehicleId, Start, Policy));
        Assert.False(reservation.CanBeFulfilledBy(Guid.CreateVersion7(), reservation.VehicleId, Start, Policy));
        Assert.False(reservation.CanBeFulfilledBy(reservation.SpotId, Guid.CreateVersion7(), Start, Policy));
        Assert.False(reservation.CanBeFulfilledBy(reservation.SpotId, reservation.VehicleId, Start.AddMinutes(-15).AddTicks(-10), Policy));
        Assert.False(reservation.CanBeFulfilledBy(reservation.SpotId, reservation.VehicleId, Start.AddMinutes(15).AddTicks(10), Policy));
    }

    [Fact]
    public void Fulfil_IsNotPartOfThePublicApi()
    {
        // Act
        var method = typeof(Reservation).GetMethod("Fulfil", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        // Assert: only ParkingSession.Open fulfils a reservation (plan 002b, proposal 7).
        Assert.NotNull(method);
        Assert.True(method.IsAssembly);
    }

    private static Reservation NewReservation()
    {
        var reservation = Reservation.Reserve(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Start, Start.AddHours(1));
        reservation.DequeueRaisedEvents();
        return reservation;
    }
}
