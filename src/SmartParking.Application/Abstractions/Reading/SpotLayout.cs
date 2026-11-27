using SmartParking.Domain.Facilities;

namespace SmartParking.Application.Abstractions.Reading;

public sealed record SpotLayout(Guid Id, Guid ZoneId, SpotType Type);
