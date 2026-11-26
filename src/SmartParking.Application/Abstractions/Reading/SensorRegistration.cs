using SmartParking.Domain.Facilities;

namespace SmartParking.Application.Abstractions.Reading;

/// <summary>
/// What the ingestion needs to know about one sensor to accept its events (spec 003, rules 1 to 7): the sensor, the
/// spot it sits on (if any) with its zone and status, and the facility status. Spot fields are <c>null</c> when the
/// sensor has no spot.
/// </summary>
public sealed record SensorRegistration(
    Guid SensorId,
    string HardwareId,
    SensorKind Kind,
    SensorStatus Status,
    Guid FacilityId,
    FacilityStatus FacilityStatus,
    Guid? SpotId,
    Guid? ZoneId,
    SpotStatus? SpotStatus);
