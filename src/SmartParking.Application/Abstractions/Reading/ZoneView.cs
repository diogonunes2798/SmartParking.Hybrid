using SmartParking.Domain.Facilities;

namespace SmartParking.Application.Abstractions.Reading;

/// <summary>Read model of a zone. <see cref="Version"/> is the <c>xmin</c>, sent as the ETag.</summary>
public sealed record ZoneView(Guid Id, Guid FacilityId, string Name, uint Version)
{
    public static ZoneView From(Zone zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        return new ZoneView(zone.Id, zone.FacilityId, zone.Name, zone.Version);
    }
}
