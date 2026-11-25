namespace SmartParking.Domain.Facilities;

/// <summary>
/// Opening interval of a facility on one day of the week (0 = Sunday … 6 = Saturday).
/// One interval per day; no row means closed that day. Part of the facility aggregate (cascade delete).
/// </summary>
public sealed class FacilityOpeningHours
{
    public FacilityOpeningHours(DayOfWeek dayOfWeek, TimeOnly opensAt, TimeOnly closesAt)
    {
        if (!Enum.IsDefined(dayOfWeek))
        {
            throw new ArgumentOutOfRangeException(nameof(dayOfWeek), dayOfWeek, "Unknown day of the week.");
        }

        DayOfWeek = dayOfWeek;
        SetInterval(opensAt, closesAt);
    }

    private FacilityOpeningHours()
    {
    }

    public DayOfWeek DayOfWeek { get; private init; }

    public TimeOnly OpensAt { get; private set; }

    public TimeOnly ClosesAt { get; private set; }

    public static bool IsValidInterval(TimeOnly opensAt, TimeOnly closesAt) => opensAt < closesAt;

    internal bool HasSameInterval(FacilityOpeningHours other) =>
        OpensAt == other.OpensAt && ClosesAt == other.ClosesAt;

    internal void SetInterval(TimeOnly opensAt, TimeOnly closesAt)
    {
        if (!IsValidInterval(opensAt, closesAt))
        {
            throw new ArgumentException("The opening time must be earlier than the closing time.", nameof(opensAt));
        }

        OpensAt = opensAt;
        ClosesAt = closesAt;
    }
}
