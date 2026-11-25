using System.Reflection;
using SmartParking.Domain.Facilities;
using SmartParking.Domain.Sessions;
using SmartParking.Domain.Sessions.Events;

namespace SmartParking.UnitTests.Domain;

/// <summary>Lifecycle of a parking session, Open → Closed → Paid (spec 002, UC-02, RF-02).</summary>
public sealed class ParkingSessionTests
{
    private static readonly DateTimeOffset Start = new(2026, 12, 2, 10, 0, 0, TimeSpan.Zero);

    private static readonly Tariff Reference = Tariff.CreateVersion(
        Guid.CreateVersion7(), Guid.CreateVersion7(), null, Start, 1.20m, 15, 10.00m, 15, "EUR", Start);

    [Fact]
    public void Open_RaisesSessionOpenedAndHasNoAmount()
    {
        // Act
        var session = NewSession();

        // Assert
        Assert.Equal(SessionStatus.Open, session.Status);
        Assert.Null(session.Amount);
        var raised = Assert.Single(session.DequeueRaisedEvents());
        Assert.IsType<SessionOpened>(raised.Event);
        Assert.Same(session, Assert.Single(raised.Changed));
    }

    [Fact]
    public void Close_After40Minutes_PricesWithItsTariffAndRaisesSessionClosed()
    {
        // Arrange
        var session = NewSession();
        session.DequeueRaisedEvents();

        // Act
        session.Close(Reference, Start.AddMinutes(40));

        // Assert
        Assert.Equal(SessionStatus.Closed, session.Status);
        Assert.Equal(0.90m, session.Amount);
        var closed = Assert.IsType<SessionClosed>(Assert.Single(session.DomainEvents));
        Assert.Equal(Start.AddMinutes(40), closed.EndedAt);
    }

    [Fact]
    public void Close_WithAnotherTariffVersion_Throws()
    {
        // Arrange
        var session = NewSession();
        var other = Tariff.CreateVersion(Guid.CreateVersion7(), Guid.CreateVersion7(), null, Start, 2m, 15, null, 0, "EUR", Start);

        // Act + Assert
        Assert.Throws<ArgumentException>(() => session.Close(other, Start.AddHours(1)));
    }

    [Fact]
    public void RecordPayment_Approved_PaysAndChangesPaymentAndSession()
    {
        // Arrange
        var session = ClosedSession();

        // Act
        var payment = session.RecordPayment(Guid.CreateVersion7(), Guid.CreateVersion7(), approved: true, Start.AddHours(1));

        // Assert
        Assert.Equal(SessionStatus.Paid, session.Status);
        Assert.Equal(PaymentStatus.Succeeded, payment.Status);
        Assert.Equal(session.Amount, payment.Amount);
        var raised = Assert.Single(session.DequeueRaisedEvents());
        Assert.IsType<SessionPaid>(raised.Event);
        Assert.Equal([payment, session], raised.Changed);
    }

    [Fact]
    public void RecordPayment_Declined_KeepsSessionClosedAndChangesOnlyThePayment()
    {
        // Arrange
        var session = ClosedSession();

        // Act
        var payment = session.RecordPayment(Guid.CreateVersion7(), Guid.CreateVersion7(), approved: false, Start.AddHours(1));

        // Assert
        Assert.Equal(SessionStatus.Closed, session.Status);
        Assert.Equal(PaymentStatus.Failed, payment.Status);
        var raised = Assert.Single(session.DequeueRaisedEvents());
        Assert.IsType<PaymentFailed>(raised.Event);
        Assert.Same(payment, Assert.Single(raised.Changed));
    }

    [Fact]
    public void RecordPayment_OpenSession_Throws()
    {
        // Act + Assert
        Assert.Throws<InvalidOperationException>(() =>
            NewSession().RecordPayment(Guid.CreateVersion7(), Guid.CreateVersion7(), approved: true, Start));
    }

    [Fact]
    public void PaidSession_CloseOrPayThrows_AC15()
    {
        // Arrange
        var session = ClosedSession();
        session.RecordPayment(Guid.CreateVersion7(), Guid.CreateVersion7(), approved: true, Start.AddHours(1));

        // Act + Assert
        Assert.Throws<InvalidOperationException>(() => session.Close(Reference, Start.AddHours(2)));
        Assert.Throws<InvalidOperationException>(() =>
            session.RecordPayment(Guid.CreateVersion7(), Guid.CreateVersion7(), approved: true, Start.AddHours(2)));
        Assert.Equal(SessionStatus.Paid, session.Status);
    }

    [Fact]
    public void ParkingSession_NoMethodLeavesPaid_AC15()
    {
        // Act: every public instance method declared by the session (not the event plumbing of AggregateRoot).
        var methods = typeof(ParkingSession)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => !method.IsSpecialName)
            .Select(method => method.Name)
            .Order(StringComparer.Ordinal);

        // Assert: Close needs Open and RecordPayment needs Closed; nothing else changes the status.
        Assert.Equal(["Close", "RecordPayment"], methods);
        Assert.All(
            typeof(ParkingSession).GetProperties().Where(property => property.Name == nameof(ParkingSession.Status)),
            property => Assert.False(property.SetMethod?.IsPublic ?? false));
    }

    private static ParkingSession NewSession() =>
        ParkingSession.Open(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Reference.Id, null, Start);

    private static ParkingSession ClosedSession()
    {
        var session = NewSession();
        session.Close(Reference, Start.AddMinutes(40));
        session.DequeueRaisedEvents();
        return session;
    }
}
