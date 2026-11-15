using SmartParking.Domain.Common;
using SmartParking.Domain.Facilities;

namespace SmartParking.Domain.Users;

/// <summary>Vehicle of a user, identified by its licence plate (personal data under GDPR).</summary>
public sealed class Vehicle : IVersioned
{
    public const int LicensePlateMaxLength = 20;

    private Vehicle()
    {
        LicensePlate = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid UserId { get; private set; }

    /// <summary>Normalised: upper case, without spaces or dashes.</summary>
    [PersonalData]
    public string LicensePlate { get; private set; }

    public VehicleType Type { get; private set; }

    public uint Version { get; private set; }

    public static Vehicle Create(Guid userId, string licensePlate, VehicleType type)
    {
        var plate = Guard.Text(licensePlate, nameof(licensePlate), LicensePlateMaxLength)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .ToUpperInvariant();

        return new Vehicle
        {
            Id = Guid.CreateVersion7(),
            UserId = Guard.Id(userId, nameof(userId)),
            LicensePlate = plate,
            Type = Guard.Defined(type, nameof(type)),
        };
    }
}
