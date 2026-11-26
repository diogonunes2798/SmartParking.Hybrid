using System.Text.Json.Nodes;
using SmartParking.Api.Tests.Fixtures;
using SmartParking.Application.Features.Ingestion;
using static SmartParking.Api.Tests.Fixtures.IngestionApi;

namespace SmartParking.Api.Tests.Features.Ingestion;

/// <summary>
/// Consistency with the registry: an inconsistent event is rejected with its code, and the rest of the batch is
/// accepted (spec 003, AC-6, AC-7, AC-8).
/// </summary>
[Collection(ApiCollectionDefinition.Name)]
public sealed class IngestionRegistryRulesTests(ApiFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 12, 3, 8, 30, 0, TimeSpan.Zero);

    private string Mongo => fixture.Databases.Mongo.GetConnectionString();

    public static TheoryData<string> RuleCodes() =>
    [
        IngestionCodes.SensorUnknown,
        IngestionCodes.SensorInactive,
        IngestionCodes.SensorNotOccupancy,
        IngestionCodes.SensorUnassigned,
        IngestionCodes.SpotMismatch,
        IngestionCodes.SpotInactive,
        IngestionCodes.FacilityInactive,
    ];

    [Theory]
    [MemberData(nameof(RuleCodes))]
    public async Task Ingest_EventFailingOneRule_RejectsItWithCodeAndAcceptsTheOther_AC6(string code)
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        fixture.Clock.SetUtcNow(Now);
        using var client = fixture.Sessions.CreateClient();
        var good = await CreateSiteAsync(client, 1, cancellationToken);
        var bad = await CreateSiteAsync(client, 2, cancellationToken);
        var invalid = await InvalidEventAsync(client, bad, code, cancellationToken);
        var valid = Event(good.Spots[0], Now);

        // Act
        var outcome = await PostOkAsync(client, Batch(invalid, valid), cancellationToken);

        // Assert
        Assert.Equal((1, 1, 0), (outcome.Accepted, outcome.Rejected, outcome.Duplicates));
        var issue = Assert.Single(outcome.Issues);
        Assert.Equal((0, IdOf(invalid), "rejected", code), (issue.Index, issue.EventId, issue.Status, issue.Code));
        var stored = await FindAsync(Mongo, [IdOf(invalid), IdOf(valid)], cancellationToken);
        Assert.Equal(IdOf(valid), Assert.Single(stored)["_id"].AsGuid);
    }

    [Fact]
    public async Task Ingest_TimestampsAroundTolerance_OnlyBeyondToleranceIsRejected_AC7()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        fixture.Clock.SetUtcNow(Now);
        using var client = fixture.Sessions.CreateClient();
        var site = await CreateSiteAsync(client, 1, cancellationToken);
        var spot = site.Spots[0];
        var atTolerance = Event(spot, Now, timestamp: Now.AddMinutes(5));
        var beyond = Event(spot, Now, timestamp: Now.AddMinutes(5).AddSeconds(1));
        var old = Event(spot, Now, timestamp: Now.AddDays(-30));

        // Act
        var outcome = await PostOkAsync(client, Batch(atTolerance, beyond, old), cancellationToken);

        // Assert
        Assert.Equal(2, outcome.Accepted);
        var issue = Assert.Single(outcome.Issues);
        Assert.Equal((1, IngestionCodes.TimestampInFuture), (issue.Index, issue.Code));
    }

    [Fact]
    public async Task Ingest_InactiveSensorWithFutureTimestamp_FirstRuleCodeWins_AC8()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        fixture.Clock.SetUtcNow(Now);
        using var client = fixture.Sessions.CreateClient();
        var site = await CreateSiteAsync(client, 1, cancellationToken);
        await UpdateSensorStatusAsync(client, site.Spots[0].SensorId, "Inactive", cancellationToken);
        var invalid = Event(site.Spots[0], Now, timestamp: Now.AddHours(1));

        // Act
        var outcome = await PostOkAsync(client, Batch(invalid), cancellationToken);

        // Assert
        Assert.Equal(IngestionCodes.SensorInactive, Assert.Single(outcome.Issues).Code);
    }

    /// <summary>An event that fails only the rule of <paramref name="code"/> (and none before it).</summary>
    private static async Task<JsonObject> InvalidEventAsync(HttpClient client, Site bad, string code, CancellationToken cancellationToken)
    {
        var spot = bad.Spots[0];
        switch (code)
        {
            case IngestionCodes.SensorUnknown:
                return Event(ConfigurationApi.NewHardwareId(), spot.SpotId, Now);
            case IngestionCodes.SensorInactive:
                await UpdateSensorStatusAsync(client, spot.SensorId, "Inactive", cancellationToken);
                return Event(spot, Now);
            case IngestionCodes.SensorNotOccupancy:
            case IngestionCodes.SensorUnassigned:
                var kind = code == IngestionCodes.SensorNotOccupancy ? "Environmental" : "Occupancy";
                var (sensor, _) = await ConfigurationApi.RegisterSensorAsync(
                    client, ConfigurationApi.NewSensor(bad.Facility.Id, kind: kind), cancellationToken);
                return Event(sensor.HardwareId, spot.SpotId, Now);
            case IngestionCodes.SpotMismatch:
                return Event(spot.HardwareId, bad.Spots[1].SpotId, Now);
            case IngestionCodes.SpotInactive:
                await UpdateSpotAsync(client, bad.Facility.Id, spot.SpotId, cancellationToken, status: "Inactive");
                return Event(spot, Now);
            case IngestionCodes.FacilityInactive:
                await DeactivateFacilityAsync(client, bad, cancellationToken);
                return Event(spot, Now);
            default:
                throw new ArgumentOutOfRangeException(nameof(code), code, "No setup for this code.");
        }
    }
}
