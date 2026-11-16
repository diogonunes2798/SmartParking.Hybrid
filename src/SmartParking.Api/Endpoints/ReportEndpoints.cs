using SmartParking.Application.Analytics;

namespace SmartParking.Api.Endpoints;

/// <summary>
/// UC-04 — analytical occupancy report (RF-05). Reads only the hourly summary table in the <c>analytics</c> schema (D4
/// default, ADR-0010), never the operational tables nor MongoDB (spec §4). Day, week and month are sums of hours, cut in
/// the report time zone (<c>Reports:TimeZone</c>, default Europe/Lisbon).
/// </summary>
internal static class ReportEndpoints
{
    public static IEndpointRouteBuilder MapReportEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/reports/occupancy", async (
                string? zone, string? from, string? to, string? granularity,
                OccupancyReportService reports, CancellationToken ct) =>
            {
                var request = OccupancyReportRequest.Create(zone, from, to, granularity);
                return TypedResults.Ok(await reports.GetAsync(request, ct));
            })
            .WithTags("Reports")
            .WithSummary("Occupancy rate by zone and period (hour, day, week, month) from the summary tables (UC-04).")
            .WithDescription(
                "from (inclusive) and to (exclusive) are whole hours in ISO 8601; without an offset they are UTC. " +
                "occupancyRate = occupied ÷ available spot-minutes. hours < expectedHours means the period is not fully aggregated.")
            .ProducesValidationProblem();

        return app;
    }
}
