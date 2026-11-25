using SmartParking.Domain.Common;

namespace SmartParking.Domain.Users;

/// <summary>
/// Vehicle of a user. The licence plate is normalised (upper case, no spaces or hyphens) and unique.
/// Created and removed through <see cref="User"/>, which raises the events.
/// </summary>
public sealed class Vehicle
{
    public const int LicensePlateMaxLength = 16;

    private Vehicle()
    {
    }

    public Guid Id { get; private init; }

    public Guid UserId { get; private init; }

    public string LicensePlate { get; private init; } = string.Empty;

    internal static Vehicle Create(Guid id, Guid userId, string licensePlate) => new()
    {
        Id = Guard.Id(id),
        UserId = Guard.Id(userId),
        LicensePlate = NormalizeLicensePlate(licensePlate),
    };

    /// <summary>
    /// Whether <see cref="NormalizeLicensePlate"/> accepts the value: only letters, digits, spaces and hyphens, and
    /// 1 to <see cref="LicensePlateMaxLength"/> characters once normalised. Used by the API validator (400).
    /// </summary>
    public static bool IsValidLicensePlate(string? licensePlate)
    {
        if (string.IsNullOrWhiteSpace(licensePlate) || !licensePlate.All(c => char.IsAsciiLetterOrDigit(c) || c is ' ' or '-'))
        {
            return false;
        }

        var length = licensePlate.Count(char.IsAsciiLetterOrDigit);
        return length is > 0 and <= LicensePlateMaxLength;
    }

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
