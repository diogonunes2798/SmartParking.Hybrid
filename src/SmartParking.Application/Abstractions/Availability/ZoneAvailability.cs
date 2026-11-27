namespace SmartParking.Application.Abstractions.Availability;

/// <summary>Free and total spots of a zone, as the availability view holds them (spec 004).</summary>
public sealed record ZoneAvailability(Guid ZoneId, string Name, int Free, int Total);
