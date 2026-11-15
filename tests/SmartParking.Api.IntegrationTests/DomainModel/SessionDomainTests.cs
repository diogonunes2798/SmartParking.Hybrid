using SmartParking.Domain.Common;
using SmartParking.Domain.Sessions;
using SmartParking.Domain.Tariffs;

namespace SmartParking.Api.IntegrationTests.DomainModel;

/// <summary>
/// Pure domain tests (no containers): the UC-02 state machine, its events, amounts (review of Phase 2a,
/// finding 5) and the provisional pricing (TODO(Diogo): rules not in the specification).
/// </summary>
public sealed class SessionDomainTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 15, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Lifecycle_raises_started_closed_and_payment_events_in_order()
    {
        var session = NewSession();
        session.Close(T0.AddHours(2), Guid.NewGuid(), 2.40m);
        var payment = session.Pay(PaymentMethod.Card, T0.AddHours(2).AddMinutes(1));

        var events = session.DequeueDomainEvents();

        Assert.Collection(
            events,
            e => Assert.IsType<SessionStarted>(e),
            e => Assert.Equal(2.40m, Assert.IsType<SessionClosed>(e).Cost),
            e => Assert.Equal(payment.Id, Assert.IsType<PaymentRegistered>(e).PaymentId));
        Assert.All(events, e => Assert.Equal(session.Id, e.AggregateId));
        Assert.Empty(session.DequeueDomainEvents());
        Assert.Equal(ParkingSessionStatus.Paid, session.Status);
        Assert.Equal(2.40m, payment.Amount);
    }

    [Fact]
    public void Paying_an_open_session_and_closing_twice_are_invalid_transitions()
    {
        var session = NewSession();
        Assert.Throws<InvalidStateTransitionException>(() => session.Pay(PaymentMethod.Card, T0));

        session.Close(T0.AddHours(1), Guid.NewGuid(), 1m);
        Assert.Throws<InvalidStateTransitionException>(() => session.Close(T0.AddHours(2), Guid.NewGuid(), 1m));
    }

    [Theory]
    [InlineData("2.405", "2.41")]
    [InlineData("2.404", "2.40")]
    public void Cost_is_rounded_to_cents_half_away_from_zero(string cost, string expected)
    {
        var session = NewSession();
        session.Close(T0.AddHours(1), Guid.NewGuid(), decimal.Parse(cost, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), session.Cost);
    }

    [Fact]
    public void Cost_out_of_the_numeric_10_2_range_is_rejected()
    {
        Assert.Throws<DomainValidationException>(() => NewSession().Close(T0.AddHours(1), Guid.NewGuid(), Money.Limit));
        Assert.Throws<DomainValidationException>(() => NewSession().Close(T0.AddHours(1), Guid.NewGuid(), -0.01m));
    }

    [Theory]
    [InlineData(1.20, 0, null, 120 * 60, 2.40)]   // 2 h at 1.20 €/h
    [InlineData(1.20, 0, null, 61, 0.04)]          // 61 s → 2 started minutes
    [InlineData(1.00, 0, null, 60, 0.02)]          // 1 min = 0.01666… → 0.02
    [InlineData(1.20, 15, null, 30 * 60, 0.30)]    // 15 free minutes, 15 billable
    [InlineData(1.20, 15, null, 10 * 60, 0.00)]    // shorter than the free period
    [InlineData(1.20, 0, 10.0, 30 * 3600, 17.20)]  // 24 h capped at 10 + 6 h at 1.20 = 7.20
    [InlineData(1.20, 0, 10.0, 48 * 3600, 20.00)]  // two full days, each capped
    public void Provisional_pricing(double rate, int freeMinutes, double? cap, int seconds, double expected)
    {
        var rules = new TariffRules((decimal)rate, freeMinutes, cap is { } c ? (decimal)c : null);
        var price = new ProvisionalTariffPricing().Price(rules, T0, T0.AddSeconds(seconds));
        Assert.Equal((decimal)expected, price);
    }

    private static ParkingSession NewSession() => ParkingSession.Open(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), T0);
}
