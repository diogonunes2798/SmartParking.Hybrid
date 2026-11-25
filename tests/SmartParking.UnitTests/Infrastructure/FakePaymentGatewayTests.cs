using SmartParking.Application.Abstractions.Payments;
using SmartParking.Infrastructure.Payments;

namespace SmartParking.UnitTests.Infrastructure;

/// <summary>The simulated gateway is deterministic and idempotent per key (spec 002; decision 1 of plan 002a).</summary>
public sealed class FakePaymentGatewayTests
{
    [Theory]
    [InlineData("tok_visa", PaymentOutcome.Approved)]
    [InlineData("decline_insufficient_funds", PaymentOutcome.Declined)]
    [InlineData("Decline_upper_case", PaymentOutcome.Approved)]
    public async Task ChargeAsync_Token_DecidesOutcome(string token, PaymentOutcome expected)
    {
        // Arrange
        var gateway = new FakePaymentGateway();

        // Act
        var outcome = await gateway.ChargeAsync(Charge(NewKey(), token), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(expected, outcome);
    }

    [Fact]
    public async Task ChargeAsync_SameKeyConcurrently_ChargesOnce()
    {
        // Arrange
        var gateway = new FakePaymentGateway();
        var key = NewKey();

        // Act
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ =>
            Task.Run(() => gateway.ChargeAsync(Charge(key, "tok_visa"), TestContext.Current.CancellationToken))));

        // Assert
        Assert.All(outcomes, outcome => Assert.Equal(PaymentOutcome.Approved, outcome));
        Assert.Equal(1, gateway.ChargeCount(key));
    }

    [Fact]
    public async Task ChargeAsync_OtherPaymentMethod_IsANewCharge()
    {
        // Arrange
        var gateway = new FakePaymentGateway();
        var sessionId = Guid.CreateVersion7();
        var declinedKey = new PaymentIdempotencyKey(sessionId, Guid.CreateVersion7());
        var otherKey = new PaymentIdempotencyKey(sessionId, Guid.CreateVersion7());
        await gateway.ChargeAsync(Charge(declinedKey, "decline_card"), TestContext.Current.CancellationToken);

        // Act
        var outcome = await gateway.ChargeAsync(Charge(otherKey, "tok_visa"), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(PaymentOutcome.Approved, outcome);
        Assert.Equal(1, gateway.ChargeCount(declinedKey));
        Assert.Equal(1, gateway.ChargeCount(otherKey));
    }

    [Fact]
    public void ChargeCount_NeverCalled_IsZero()
    {
        // Act + Assert
        Assert.Equal(0, new FakePaymentGateway().ChargeCount(NewKey()));
    }

    private static PaymentIdempotencyKey NewKey() => new(Guid.CreateVersion7(), Guid.CreateVersion7());

    private static PaymentCharge Charge(PaymentIdempotencyKey key, string token) => new(key, token, 0.90m, "EUR");
}
