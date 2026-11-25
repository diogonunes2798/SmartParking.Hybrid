using SmartParking.Application.Abstractions;
using SmartParking.Application.Abstractions.Messaging;
using SmartParking.Application.Abstractions.Persistence;
using SmartParking.Application.Abstractions.Reading;
using SmartParking.Domain.Users;

namespace SmartParking.Application.Features.Users;

/// <summary>
/// Registers a driver (UC-02, spec 002 AC-2). The email is stored in lower case; a repeated email, in any case, is
/// refused by the unique index (409). One audit entry and one <c>UserRegistered.v1</c> in the same transaction.
/// </summary>
public static class RegisterUser
{
    public sealed record Command(string Name, string Email);

    internal sealed class Handler(IUserRepository users, IUnitOfWork unitOfWork, TimeProvider clock)
        : ICommandHandler<Command, UserView>
    {
        public async Task<Result<UserView>> HandleAsync(Command command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            var user = User.Register(Guid.CreateVersion7(clock.GetUtcNow()), command.Name, command.Email);
            users.Add(user);

            var saved = await unitOfWork.SaveChangesAsync(cancellationToken);
            if (saved.Error is { } error)
            {
                return error;
            }

            return UserView.From(user);
        }
    }
}
