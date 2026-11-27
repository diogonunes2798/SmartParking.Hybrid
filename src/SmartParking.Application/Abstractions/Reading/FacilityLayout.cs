namespace SmartParking.Application.Abstractions.Reading;

/// <summary>
/// An active facility with its zones and active spots. <see cref="Zones"/> lists only the zones that have active
/// spots, because the view keeps only those (spec 004, «Estruturas Redis»).
/// </summary>
public sealed record FacilityLayout(
    Guid Id,
    string Name,
    Guid OperatorId,
    double Latitude,
    double Longitude,
    IReadOnlyList<ZoneLayout> Zones,
    IReadOnlyList<SpotLayout> Spots);
