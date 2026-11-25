using SmartParking.Application.Abstractions;
using SmartParking.Application.Abstractions.Messaging;
using SmartParking.Application.Abstractions.Payments;
using SmartParking.Application.Abstractions.Persistence;
using SmartParking.Application.Abstractions.Reading;
using SmartParking.Domain.Sessions;

namespace SmartParking.Application.Features.Sessions;

/// <summary>
/// Pays a closed session (UC-02, RF-02; spec 002 AC-12 to AC-18), in the order of the spec:
/// <list type="number">
/// <item>preconditions, without calling the gateway or writing anything: the session is <c>Closed</c> and the
/// payment method belongs to the owner of the session's vehicle (409);</item>
/// <item>the gateway, outside the transaction, with the idempotency key (session, payment method); a 0.00 session
/// is approved without calling it;</item>
/// <item>one transaction with the payment, the session (when approved), the audit entries and one outbox message.</item>
/// </list>
/// If the save fails after an approval, nothing is written and a retry with the same method is not charged twice.
/// A decline is recorded as a failed payment and answered with 402 and its <c>paymentId</c>.
/// </summary>
public static class PaySession
{
    public sealed record Command(Guid Id, Guid PaymentMethodId);

    internal sealed class Handler(
        IParkingSessionRepository sessions,
        IUserRepository users,
        ITariffRepository tariffs,
        IPaymentGateway gateway,
        IUnitOfWork unitOfWork,
        TimeProvider clock) : ICommandHandler<Command, PaymentReceipt>
    {
        public async Task<Result<PaymentReceipt>> HandleAsync(Command command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            var session = await sessions.GetByIdAsync(command.Id, cancellationToken);
            if (session is null)
            {
                return SessionErrors.SessionNotFound(command.Id);
            }

            if (session.Status != SessionStatus.Closed)
            {
                return SessionErrors.NotClosed;
            }

            var method = await users.FindPaymentMethodAsync(command.PaymentMethodId, cancellationToken);
            if (method is null)
            {
                return SessionErrors.PaymentMethodNotFound(command.PaymentMethodId);
            }

            var vehicle = await users.FindVehicleAsync(session.VehicleId, cancellationToken)
                ?? throw new InvalidOperationException($"Session {session.Id} points to a missing vehicle.");
            if (method.UserId != vehicle.UserId)
            {
                return SessionErrors.PaymentMethodOfAnotherUser;
            }

            var tariff = await tariffs.GetByIdAsync(session.TariffId, cancellationToken)
                ?? throw new InvalidOperationException($"Session {session.Id} points to a missing tariff version.");
            var amount = session.Amount!.Value;

            var approved = amount == 0m
                || await gateway.ChargeAsync(
                    new PaymentCharge(new PaymentIdempotencyKey(session.Id, method.Id), method.ProviderToken, amount, tariff.Currency),
                    cancellationToken) == PaymentOutcome.Approved;

            var now = clock.GetUtcNow();
            var payment = session.RecordPayment(Guid.CreateVersion7(now), method.Id, approved, now);
            sessions.AddPayment(payment);

            var saved = await unitOfWork.SaveChangesAsync(cancellationToken);
            if (saved.Error is { } error)
            {
                return SessionErrors.FromSave(error);
            }

            if (!approved)
            {
                return SessionErrors.PaymentDeclined(payment.Id);
            }

            return new PaymentReceipt(SessionView.From(session, tariff.Currency), payment.Id);
        }
    }
}
