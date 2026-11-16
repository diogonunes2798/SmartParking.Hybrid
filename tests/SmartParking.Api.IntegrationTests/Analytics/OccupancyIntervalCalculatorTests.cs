using SmartParking.Application.Analytics;
using SmartParking.Domain.Occupancy;

namespace SmartParking.Api.IntegrationTests.Analytics;

/// <summary>Pure rules of the hourly occupancy computation (UC-04, ADR-0010). No containers.</summary>
public sealed class OccupancyIntervalCalculatorTests
{
    private static readonly DateTimeOffset Ten = new(2026, 11, 10, 10, 0, 0, TimeSpan.Zero);
    private static readonly Guid Spot = Guid.NewGuid();
    private static readonly Dictionary<Guid, OccupancyEventType> NoState = [];

    private static SpotOccupancyEvent At(DateTimeOffset when, OccupancyEventType type, Guid? spot = null, Guid? id = null) =>
        new(id ?? Guid.NewGuid(), spot ?? Spot, type, when);

    [Fact]
    public void One_spot_occupied_from_10_00_to_10_30_is_half_of_the_10_hour()
    {
        var hours = OccupancyIntervalCalculator.Compute(
            Ten, Ten.AddHours(1), [Spot], NoState,
            [At(Ten, OccupancyEventType.Occupied), At(Ten.AddMinutes(30), OccupancyEventType.Freed)]);

        var hour = Assert.Single(hours);
        Assert.Equal(Ten, hour.HourStart);
        Assert.Equal(30 * 60 * 1000, hour.OccupiedMilliseconds);
        Assert.Equal(2, hour.EventCount);
        Assert.Equal(0.5, hour.OccupiedMilliseconds / 3_600_000d);
    }

    [Fact]
    public void An_occupation_across_an_hour_boundary_is_split_between_the_hours()
    {
        var hours = OccupancyIntervalCalculator.Compute(
            Ten, Ten.AddHours(2), [Spot], NoState,
            [At(Ten.AddMinutes(45), OccupancyEventType.Occupied), At(Ten.AddMinutes(80), OccupancyEventType.Freed)]);

        Assert.Equal([15 * 60_000L, 20 * 60_000L], hours.Select(h => h.OccupiedMilliseconds));
        Assert.Equal([1, 1], hours.Select(h => h.EventCount));
    }

    [Fact]
    public void A_spot_occupied_before_the_range_counts_from_the_start_of_the_range()
    {
        // Occupied at 9:50 (before the range: initial state), freed at 10:20.
        var hours = OccupancyIntervalCalculator.Compute(
            Ten, Ten.AddHours(1), [Spot], new Dictionary<Guid, OccupancyEventType> { [Spot] = OccupancyEventType.Occupied },
            [At(Ten.AddMinutes(20), OccupancyEventType.Freed)]);

        Assert.Equal(20 * 60_000L, Assert.Single(hours).OccupiedMilliseconds);
    }

    [Fact]
    public void A_spot_still_occupied_at_the_end_counts_until_the_end_of_the_range()
    {
        var hours = OccupancyIntervalCalculator.Compute(
            Ten, Ten.AddHours(2), [Spot], NoState, [At(Ten.AddMinutes(30), OccupancyEventType.Occupied)]);

        Assert.Equal([30 * 60_000L, 60 * 60_000L], hours.Select(h => h.OccupiedMilliseconds));
    }

    [Fact]
    public void Occupied_twice_is_one_occupation_and_freed_while_free_changes_nothing()
    {
        var hours = OccupancyIntervalCalculator.Compute(
            Ten, Ten.AddHours(1), [Spot], NoState,
            [
                At(Ten.AddMinutes(5), OccupancyEventType.Freed),
                At(Ten.AddMinutes(10), OccupancyEventType.Occupied),
                At(Ten.AddMinutes(20), OccupancyEventType.Occupied),
                At(Ten.AddMinutes(40), OccupancyEventType.Freed),
                At(Ten.AddMinutes(50), OccupancyEventType.Freed),
            ]);

        var hour = Assert.Single(hours);
        Assert.Equal(30 * 60_000L, hour.OccupiedMilliseconds);
        Assert.Equal(5, hour.EventCount);
    }

    [Fact]
    public void Events_arrive_in_any_order_and_ties_are_broken_by_event_id()
    {
        var low = new Guid("00000000-0000-0000-0000-000000000001");
        var high = new Guid("00000000-0000-0000-0000-000000000002");
        var hours = OccupancyIntervalCalculator.Compute(
            Ten, Ten.AddHours(1), [Spot], NoState,
            [
                At(Ten.AddMinutes(30), OccupancyEventType.Freed, id: high), // same timestamp, larger id: applied last
                At(Ten.AddMinutes(30), OccupancyEventType.Occupied, id: low),
                At(Ten, OccupancyEventType.Occupied),
            ]);

        // 10:00 occupied; 10:30 occupied (no change) then freed → 30 minutes.
        Assert.Equal(30 * 60_000L, Assert.Single(hours).OccupiedMilliseconds);
    }

    [Fact]
    public void An_event_exactly_on_the_boundary_belongs_to_the_hour_that_starts_there()
    {
        var hours = OccupancyIntervalCalculator.Compute(
            Ten, Ten.AddHours(2), [Spot], NoState,
            [At(Ten, OccupancyEventType.Occupied), At(Ten.AddHours(1), OccupancyEventType.Freed)]);

        Assert.Equal([60 * 60_000L, 0L], hours.Select(h => h.OccupiedMilliseconds));
        Assert.Equal([1, 1], hours.Select(h => h.EventCount));
    }

    [Fact]
    public void Unregistered_spots_count_as_events_but_not_as_occupied_time()
    {
        var unknown = Guid.NewGuid();
        var hours = OccupancyIntervalCalculator.Compute(
            Ten, Ten.AddHours(1), [Spot], NoState, [At(Ten, OccupancyEventType.Occupied, unknown)]);

        var hour = Assert.Single(hours);
        Assert.Equal(0, hour.OccupiedMilliseconds);
        Assert.Equal(1, hour.EventCount);
    }

    [Fact]
    public void Several_spots_add_up_and_never_exceed_the_available_time()
    {
        var other = Guid.NewGuid();
        var hours = OccupancyIntervalCalculator.Compute(
            Ten, Ten.AddHours(1), [Spot, other],
            new Dictionary<Guid, OccupancyEventType> { [Spot] = OccupancyEventType.Occupied, [other] = OccupancyEventType.Occupied },
            [At(Ten.AddMinutes(10), OccupancyEventType.Occupied)]);

        Assert.Equal(2 * 60 * 60_000L, Assert.Single(hours).OccupiedMilliseconds);
    }

    [Fact]
    public void Events_outside_the_range_are_ignored()
    {
        var hours = OccupancyIntervalCalculator.Compute(
            Ten, Ten.AddHours(1), [Spot], NoState,
            [At(Ten.AddMinutes(-1), OccupancyEventType.Occupied), At(Ten.AddHours(1), OccupancyEventType.Occupied)]);

        var hour = Assert.Single(hours);
        Assert.Equal(0, hour.OccupiedMilliseconds);
        Assert.Equal(0, hour.EventCount);
    }

    [Fact]
    public void The_range_must_be_whole_hours()
    {
        Assert.Throws<ArgumentException>(() =>
            OccupancyIntervalCalculator.Compute(Ten.AddMinutes(1), Ten.AddHours(1), [Spot], NoState, []));
        Assert.Throws<ArgumentException>(() =>
            OccupancyIntervalCalculator.Compute(Ten, Ten, [Spot], NoState, []));
    }
}
