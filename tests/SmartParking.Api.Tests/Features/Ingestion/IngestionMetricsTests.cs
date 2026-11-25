using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using SmartParking.Api.Tests.Fixtures;
using SmartParking.Application.Features.Ingestion;
using static SmartParking.Api.Tests.Fixtures.IngestionApi;

namespace SmartParking.Api.Tests.Features.Ingestion;

/// <summary>The SmartParking.Ingestion meter (spec 003, AC-17), read with a BCL <see cref="MeterListener"/>.</summary>
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

        // Only this host's meter: other hosts in the process create their own instance of the same meter name.
        var meterFactory = factory.Services.GetRequiredService<IMeterFactory>();
        List<(string Instrument, double Value, Dictionary<string, object?> Tags)> measurements = [];
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == IngestionMetrics.MeterName && ReferenceEquals(instrument.Meter.Scope, meterFactory))
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Record(instrument, value, tags));
        listener.SetMeasurementEventCallback<int>((instrument, value, tags, _) => Record(instrument, value, tags));
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Record(instrument, value, tags));
        listener.Start();

        // Act
        await PostOkAsync(client, Batch(accepted, duplicate, unknown), cancellationToken);

        // Assert
        List<(string Instrument, double Value, Dictionary<string, object?> Tags)> recorded;
        lock (measurements)
        {
            recorded = [.. measurements];
        }

        var events = recorded.Where(m => m.Instrument == "ingestion.events").ToList();
        Assert.Equal(1, events.Where(m => Equals(m.Tags[IngestionMetrics.OutcomeTag], IngestionMetrics.Accepted)).Sum(m => m.Value));
        Assert.Equal(1, events.Where(m => Equals(m.Tags[IngestionMetrics.OutcomeTag], IngestionMetrics.Duplicate)).Sum(m => m.Value));
        var rejected = Assert.Single(events, m => Equals(m.Tags[IngestionMetrics.OutcomeTag], IngestionMetrics.Rejected));
        Assert.Equal((1d, (object?)IngestionCodes.SensorUnknown), (rejected.Value, rejected.Tags[IngestionMetrics.CodeTag]));
        Assert.Equal(3d, Assert.Single(recorded, m => m.Instrument == "ingestion.batch.size").Value);

        void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var copy = tags.ToArray().ToDictionary(tag => tag.Key, tag => tag.Value);
            lock (measurements)
            {
                measurements.Add((instrument.Name, value, copy));
            }
        }
    }
}
