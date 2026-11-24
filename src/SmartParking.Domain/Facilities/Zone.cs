using SmartParking.Domain.Common;

namespace SmartParking.Domain.Facilities;

/// <summary>Zone of a parking facility (for example a floor). Behaviour and events arrive in F2b.</summary>
public sealed class Zone : AggregateRoot
{
    public const int NameMaxLength = 100;

    private Zone()
    {
    }

    public Guid FacilityId { get; private init; }

    public string Name { get; private set; } = string.Empty;

    public uint Version { get; private set; }

    public static Zone Create(Guid id, Guid facilityId, string name) => new()
    {
        Id = Guard.Id(id),
        FacilityId = Guard.Id(facilityId),
        Name = Guard.Text(name, NameMaxLength),
    };
}
