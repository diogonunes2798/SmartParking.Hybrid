using SmartParking.Domain.Facilities;

namespace SmartParking.Application.Abstractions.Reading;

/// <summary>Optional filters of the spot list of a facility.</summary>
public sealed record SpotFilter(Guid? ZoneId = null, SpotStatus? Status = null);
