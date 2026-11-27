using SmartParking.Api.Tests.Fixtures;
using SmartParking.Application.Features.Ingestion;
using static SmartParking.Api.Tests.Fixtures.IngestionApi;

namespace SmartParking.Api.Tests.Features.Ingestion;

/// <summary>The SmartParking.Ingestion meter (spec 003, AC-17).</summary>
[Collection(ApiCollectionDefinition.Name)]
public sealed class IngestionMetricsTests(ApiFixture fixture)
{
    private static readonly DateTimeOffset Past = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Ingest_AcceptedDuplicateAndRejected_RecordsOneOfEachAndTheBatchSize_AC17()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = fixture.CreateFactory();
        using var client = factory.CreateClient();
        var site = await CreateSiteAsync(client, 1, cancellationToken);
        var accepted = Event(site.Spots[0], Past);
        var duplicate = Event(site.Spots[0], Past, eventId: IdOf(accepted));
        var unknown = Event(ConfigurationApi.NewHardwareId(), site.Spots[0].SpotId, Past);
        using var recorder = new MeterRecorder(factory.Services, IngestionMetrics.MeterName);

        // Act
        await PostOkAsync(client, Batch(accepted, duplicate, unknown), cancellationToken);

        // Assert
        var recorded = recorder.Snapshot();
        var events = recorded.Where(m => m.Instrument == "ingestion.events").ToList();
        Assert.Equal(1, events.Where(m => Equals(m.Tags[IngestionMetrics.OutcomeTag], IngestionMetrics.Accepted)).Sum(m => m.Value));
        Assert.Equal(1, events.Where(m => Equals(m.Tags[IngestionMetrics.OutcomeTag], IngestionMetrics.Duplicate)).Sum(m => m.Value));
        var rejected = Assert.Single(events, m => Equals(m.Tags[IngestionMetrics.OutcomeTag], IngestionMetrics.Rejected));
        Assert.Equal((1d, (object?)IngestionCodes.SensorUnknown), (rejected.Value, rejected.Tags[IngestionMetrics.CodeTag]));
        Assert.Equal(3d, Assert.Single(recorded, m => m.Instrument == "ingestion.batch.size").Value);
    }
}
