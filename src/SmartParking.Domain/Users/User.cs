using SmartParking.Domain.Common;

namespace SmartParking.Domain.Users;

/// <summary>
/// Driver using the parking service. Minimal personal data only (RNF-06, GDPR data minimisation):
/// a display name and a contact e-mail, both masked in the audit log.
/// </summary>
public sealed class User : IVersioned
{
    public const int DisplayNameMaxLength = 200;
    public const int EmailMaxLength = 320;

    private User()
    {
        DisplayName = Email = string.Empty;
    }

    public Guid Id { get; private set; }

    [PersonalData]
    public string DisplayName { get; private set; }

    [PersonalData]
    public string Email { get; private set; }

    public uint Version { get; private set; }

    public static User Create(string displayName, string email)
    {
        var normalisedEmail = Guard.Text(email, nameof(email), EmailMaxLength).ToLowerInvariant();
        if (!normalisedEmail.Contains('@', StringComparison.Ordinal))
        {
            throw new DomainValidationException(nameof(email), "is not a valid e-mail address");
        }

        return new User
        {
            Id = Guid.CreateVersion7(),
            DisplayName = Guard.Text(displayName, nameof(displayName), DisplayNameMaxLength),
            Email = normalisedEmail,
        };
    }
}
