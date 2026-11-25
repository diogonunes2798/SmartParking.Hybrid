using SmartParking.Application.Abstractions;
using SmartParking.Application.Abstractions.Messaging;
using SmartParking.Application.Abstractions.Persistence;

namespace SmartParking.Application.Features.Users;

/// <summary>
/// Removes a payment method of a user. While a payment refers to it, the database refuses it (FK RESTRICT) and the
/// whole transaction is rolled back (409).
/// </summary>
public static class RemovePaymentMethod
{
    public sealed record Command(Guid UserId, Guid PaymentMethodId);

    internal sealed class Handler(IUserRepository users, IUnitOfWork unitOfWork) : ICommandHandler<Command, Guid>
    {
        public async Task<Result<Guid>> HandleAsync(Command command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            var user = await users.GetByIdAsync(command.UserId, cancellationToken);
            if (user is null)
            {
                return UserErrors.NotFound(command.UserId);
            }

            var method = await users.GetPaymentMethodAsync(command.UserId, command.PaymentMethodId, cancellationToken);
            if (method is null)
            {
                return UserErrors.PaymentMethodNotFound(command.PaymentMethodId);
            }

            user.RemovePaymentMethod(method);
            users.RemovePaymentMethod(method);

            var saved = await unitOfWork.SaveChangesAsync(cancellationToken);
            if (saved.Error is { } error)
            {
                return error;
            }

            return method.Id;
        }
    }
}
