namespace SmartParking.Application.Abstractions.Payments;

/// <summary>
/// Port to the payment provider (spec 002). Called outside the database transaction, before the payment is recorded.
/// Charging again with the same <see cref="PaymentIdempotencyKey"/> returns the earlier outcome without a new charge.
/// </summary>
public interface IPaymentGateway
{
    Task<PaymentOutcome> ChargeAsync(PaymentCharge charge, CancellationToken cancellationToken);
}
