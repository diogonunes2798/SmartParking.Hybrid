using System.Collections.Concurrent;
using SmartParking.Application.Abstractions.Payments;

namespace SmartParking.Infrastructure.Payments;

/// <summary>
/// Simulated, deterministic payment gateway (spec 002; decision 1 of plan 002a). It declines when the provider token
/// starts with <c>decline_</c> and approves every other one. The outcome is kept per idempotency key, so a repeated
/// charge returns it without a new charge; <see cref="Lazy{T}"/> makes two concurrent first charges with the same key
/// charge once (AC-16). The state lives in memory and is lost on restart, which the spec accepts. Never logs the token.
/// </summary>
internal sealed class FakePaymentGateway : IPaymentGateway
{
    public const string DeclinePrefix = "decline_";

    private readonly ConcurrentDictionary<PaymentIdempotencyKey, Lazy<PaymentOutcome>> outcomes = new();

    private readonly ConcurrentDictionary<PaymentIdempotencyKey, int> charges = new();

    public Task<PaymentOutcome> ChargeAsync(PaymentCharge charge, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(charge);
        cancellationToken.ThrowIfCancellationRequested();

        var outcome = outcomes.GetOrAdd(charge.Key, key => new Lazy<PaymentOutcome>(() => Charge(key, charge.ProviderToken)));
        return Task.FromResult(outcome.Value);
    }

    /// <summary>Charges actually made for a key: 0 when the gateway was never called, 1 after any number of repeats.</summary>
    public int ChargeCount(PaymentIdempotencyKey key) => charges.GetValueOrDefault(key);

    private PaymentOutcome Charge(PaymentIdempotencyKey key, string providerToken)
    {
        charges.AddOrUpdate(key, 1, (_, count) => count + 1);
        return providerToken.StartsWith(DeclinePrefix, StringComparison.Ordinal) ? PaymentOutcome.Declined : PaymentOutcome.Approved;
    }
}
