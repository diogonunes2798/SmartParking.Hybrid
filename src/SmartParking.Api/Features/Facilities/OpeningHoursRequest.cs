namespace SmartParking.Api.Features.Facilities;

/// <summary>Opening interval of one day: <c>dayOfWeek</c> 0 (Sunday) to 6 (Saturday), times as <c>HH:mm:ss</c>.</summary>
internal sealed record OpeningHoursRequest(int DayOfWeek, TimeOnly OpensAt, TimeOnly ClosesAt);
