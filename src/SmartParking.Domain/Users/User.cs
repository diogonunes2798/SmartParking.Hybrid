using SmartParking.Domain.Common;

namespace SmartParking.Domain.Users;

/// <summary>
/// Driver account. Name and email are personal data: never log them at Information level or below.
/// Structure only in F2; endpoints arrive in F3.
/// </summary>
public sealed class User : AggregateRoot
{
    public const int NameMaxLength = 200;

    public const int EmailMaxLength = 254;

    private User()
    {
    }

    public string Name { get; private set; } = string.Empty;

    /// <summary>Stored in lower case; unique.</summary>
    public string Email { get; private set; } = string.Empty;

    public static User Create(Guid id, string name, string email) => new()
    {
        Id = Guard.Id(id),
        Name = Guard.Text(name, NameMaxLength),
        Email = NormalizeEmail(email),
    };

    public static string NormalizeEmail(string email) =>
        Guard.Text(email, EmailMaxLength).ToLowerInvariant();
}
