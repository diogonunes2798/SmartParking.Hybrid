using SmartParking.Domain.Users;

namespace SmartParking.Application.Abstractions.Reading;

/// <summary>Read model of a user. Name and email are personal data: never log them at Information level or below.</summary>
public sealed record UserView(Guid Id, string Name, string Email)
{
    public static UserView From(User user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return new UserView(user.Id, user.Name, user.Email);
    }
}
