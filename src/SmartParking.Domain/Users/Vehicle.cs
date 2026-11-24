using SmartParking.Domain.Common;

namespace SmartParking.Domain.Users;

/// <summary>Vehicle of a user. The licence plate is normalised (upper case, no spaces or hyphens) and unique.</summary>
public sealed class Vehicle
{
    public const int LicensePlateMaxLength = 16;

    private Vehicle()
    {
    }

    public Guid Id { get; private init; }

    public Guid UserId { get; private init; }

    public string LicensePlate { get; private init; } = string.Empty;

    public static Vehicle Create(Guid id, Guid userId, string licensePlate) => new()
    {
        Id = Guard.Id(id),
        UserId = Guard.Id(userId),
        LicensePlate = NormalizeLicensePlate(licensePlate),
    };

    public static string NormalizeLicensePlate(string licensePlate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(licensePlate);

        var normalized = string.Concat(licensePlate.Where(c => c is not (' ' or '-'))).ToUpperInvariant();
        if (!normalized.All(char.IsAsciiLetterOrDigit))
        {
            throw new ArgumentException("A licence plate has only letters, digits, spaces and hyphens.", nameof(licensePlate));
        }

        return Guard.Text(normalized, LicensePlateMaxLength, nameof(licensePlate));
    }
}
