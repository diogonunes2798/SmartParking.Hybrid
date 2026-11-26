namespace SmartParking.SensorSimulator.Wire;

/// <summary>Opening interval of one day: <c>dayOfWeek</c> 0 (Sunday) to 6 (Saturday).</summary>
internal sealed record OpeningHoursRequest(int DayOfWeek, TimeOnly OpensAt, TimeOnly ClosesAt);
