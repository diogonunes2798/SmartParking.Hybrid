namespace SmartParking.Application.Abstractions.Reading;

/// <summary>The active facilities as the availability view needs them.</summary>
public sealed record AvailabilityLayout(IReadOnlyList<FacilityLayout> Facilities);
