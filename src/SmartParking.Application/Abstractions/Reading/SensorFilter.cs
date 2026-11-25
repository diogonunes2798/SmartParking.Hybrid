using SmartParking.Domain.Facilities;

namespace SmartParking.Application.Abstractions.Reading;

/// <summary>Optional filters of the sensor list.</summary>
public sealed record SensorFilter(Guid? FacilityId = null, SensorKind? Kind = null);
