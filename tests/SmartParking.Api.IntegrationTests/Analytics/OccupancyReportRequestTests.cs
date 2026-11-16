using SmartParking.Application.Analytics;
using SmartParking.Domain.Common;

namespace SmartParking.Api.IntegrationTests.Analytics;

/// <summary>Validation of the report parameters (UC-04, ADR-0010). No containers. The periods are tested end to end.</summary>
public sealed class OccupancyReportRequestTests
{
    [Fact]
    public void Defaults_to_hour_and_treats_a_date_without_offset_as_utc()
    {
        var request = OccupancyReportRequest.Create(null, "2026-11-10T10:00:00", "2026-11-10T12:00:00", null);

        Assert.Equal(ReportGranularity.Hour, request.Granularity);
        Assert.Equal(new DateTimeOffset(2026, 11, 10, 10, 0, 0, TimeSpan.Zero), request.From);
        Assert.Null(request.Zone);
    }

    [Fact]
    public void Accepts_an_offset_and_returns_utc()
    {
        var request = OccupancyReportRequest.Create("Centro", "2026-07-01T00:00:00+01:00", "2026-07-02T00:00:00+01:00", "DAY");

        Assert.Equal(new DateTimeOffset(2026, 6, 30, 23, 0, 0, TimeSpan.Zero), request.From);
        Assert.Equal(TimeSpan.Zero, request.From.Offset);
        Assert.Equal(ReportGranularity.Day, request.Granularity);
    }

    [Theory]
    [InlineData(null, "2026-11-10T00:00Z", "day", "from")]
    [InlineData("2026-11-10T00:00Z", null, "day", "to")]
    [InlineData("yesterday", "2026-11-10T00:00Z", "day", "from")]
    [InlineData("2026-11-10T00:30Z", "2026-11-11T00:00Z", "day", "from")]
    [InlineData("2026-11-10T00:00Z", "2026-11-10T00:00Z", "day", "to")]
    [InlineData("2026-11-10T00:00Z", "2026-11-11T00:00Z", "year", "granularity")]
    [InlineData("2026-11-10T00:00Z", "2026-11-11T00:00Z", "2", "granularity")]
    [InlineData("2026-11-10T00:00Z", "2026-11-11T00:00Z", "hour,day", "granularity")] // Enum.TryParse would give week
    [InlineData("11/10/2026 00:00", "2026-11-11T00:00Z", "day", "from")] // not ISO 8601
    [InlineData("2026-11-10T00:00Z", "Nov 11 2026 00:00", "day", "to")]
    [InlineData("2026-11-10T00:00 01:00", "2026-11-11T00:00Z", "day", "from")] // a + not encoded in the URL
    [InlineData("2026-01-01T00:00Z", "2026-03-01T00:00Z", "hour", "to")]
    [InlineData("2020-01-01T00:00Z", "2026-01-01T00:00Z", "month", "to")]
    public void Rejects_invalid_parameters(string? from, string? to, string? granularity, string field)
    {
        var ex = Assert.Throws<DomainValidationException>(() => OccupancyReportRequest.Create(null, from, to, granularity));

        Assert.Equal(field, ex.Field);
    }

    [Fact]
    public void Rejects_a_blank_zone()
    {
        var ex = Assert.Throws<DomainValidationException>(() =>
            OccupancyReportRequest.Create(" ", "2026-11-10T00:00Z", "2026-11-11T00:00Z", "day"));

        Assert.Equal("zone", ex.Field);
    }

    [Fact]
    public void An_unknown_time_zone_fails_at_startup()
    {
        Assert.ThrowsAny<Exception>(() => new ReportSettings { TimeZone = "Mars/Olympus" }.Resolve());
    }
}
