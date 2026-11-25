namespace SmartParking.Application.Abstractions.Reading;

/// <summary>Opening interval of one day of the week (0 = Sunday … 6 = Saturday).</summary>
public sealed record OpeningHoursView(DayOfWeek DayOfWeek, TimeOnly OpensAt, TimeOnly ClosesAt);
