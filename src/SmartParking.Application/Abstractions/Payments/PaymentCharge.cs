namespace SmartParking.Application.Abstractions.Payments;

/// <summary>
/// One charge request. <see cref="ProviderToken"/> is a credential: never log it, never return it (spec 002).
/// <see cref="ToString"/> leaves it out, so a record printed by mistake does not leak it.
/// </summary>
public sealed record PaymentCharge(PaymentIdempotencyKey Key, string ProviderToken, decimal Amount, string Currency)
{
    public override string ToString() => $"PaymentCharge {{ Key = {Key}, Amount = {Amount}, Currency = {Currency} }}";
}
