using SmartParking.Application.Abstractions;
using SmartParking.Application.Abstractions.Messaging;
using SmartParking.Application.Abstractions.Persistence;
using SmartParking.Application.Abstractions.Reading;
using SmartParking.Domain.Users;

namespace SmartParking.Application.Features.Users;

/// <summary>
/// Adds a payment method of a user. The provider token is stored but never returned, audited, put in the outbox or
/// logged (spec 002 AC-4). The user raises <c>PaymentMethodAdded.v1</c> with the method as the only changed entity.
/// </summary>
public static class AddPaymentMethod
{
    public sealed record Command(Guid UserId, PaymentMethodType Type, string ProviderToken, string MaskedNumber, DateOnly? ExpiresOn)
    {
        /// <summary>Leaves the provider token out, so a command printed by mistake does not leak it.</summary>
        public override string ToString() =>
            $"Command {{ UserId = {UserId}, Type = {Type}, MaskedNumber = {MaskedNumber}, ExpiresOn = {ExpiresOn} }}";
    }

    internal sealed class Handler(IUserRepository users, IUnitOfWork unitOfWork, TimeProvider clock)
        : ICommandHandler<Command, PaymentMethodView>
    {
        public async Task<Result<PaymentMethodView>> HandleAsync(Command command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            var user = await users.GetByIdAsync(command.UserId, cancellationToken);
            if (user is null)
            {
                return UserErrors.NotFound(command.UserId);
            }

            var method = user.AddPaymentMethod(
                Guid.CreateVersion7(clock.GetUtcNow()), command.Type, command.ProviderToken, command.MaskedNumber, command.ExpiresOn);
            users.AddPaymentMethod(method);

            var saved = await unitOfWork.SaveChangesAsync(cancellationToken);
            if (saved.Error is { } error)
            {
                return error;
            }

            return PaymentMethodView.From(method);
        }
    }
}
