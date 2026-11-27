using Microsoft.Extensions.DependencyInjection;
using SmartParking.Api.Tests.Fixtures;
using SmartParking.Application.Abstractions.Availability;
using SmartParking.Application.Abstractions.Messaging;
using SmartParking.Application.Features.Availability;
using SmartParking.Domain.Occupancy;
using static SmartParking.Api.Tests.Fixtures.AvailabilityView;
using static SmartParking.Api.Tests.Fixtures.IngestionApi;

namespace SmartParking.Api.Tests.Features.Availability;

/// <summary>The SmartParking.Availability meter (spec 004, AC-22).</summary>
[Collection(ApiCollectionDefinition.Name)]
public sealed class AvailabilityMetricsTests(ApiFixture fixture)
{
    private static readonly DateTimeOffset T = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Projector_AppliedDuplicateStaleAndUnknown_RecordsOneOfEach_AC22()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = fixture.CreateFactory();
        using var client = factory.CreateClient();
        var site = await CreateSiteAsync(client, 1, cancellationToken);
        await SeedAsync(factory.Services, site, cancellationToken);
        var projector = factory.Services.GetRequiredService<IOccupancyProjector>();
        var applied = Occupancy(site, site.Spots[0], OccupancyEventType.Occupied, T);
        using var recorder = new MeterRecorder(factory.Services, AvailabilityMetrics.MeterName);

        // Act
        await projector.ApplyAsync(applied, false, cancellationToken);
        await projector.ApplyAsync(applied, false, cancellationToken);
        await projector.ApplyAsync(Occupancy(site, site.Spots[0], OccupancyEventType.Freed, T.AddSeconds(-1)), false, cancellationToken);
        await projector.ApplyAsync(
            Occupancy(site, site.Spots[0] with { SpotId = Guid.NewGuid() }, OccupancyEventType.Occupied, T), false, cancellationToken);

        // Assert
        var events = recorder.Snapshot().Where(m => m.Instrument == AvailabilityMetrics.ProjectorEventsInstrument).ToList();
        foreach (var outcome in new[] { AvailabilityMetrics.Applied, AvailabilityMetrics.Duplicate, AvailabilityMetrics.Stale, AvailabilityMetrics.UnknownSpot })
        {
            Assert.Equal(1, events.Where(m => Equals(m.Tags[AvailabilityMetrics.OutcomeTag], outcome)).Sum(m => m.Value));
        }
    }

    [Fact]
    public async Task Rebuild_OneRun_RecordsOneDuration_AC22()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        var environment = await CreateIsolatedAsync(fixture, cancellationToken);
        await using var factory = ApiFixture.CreateFactory(environment.Api);
        using var recorder = new MeterRecorder(factory.Services, AvailabilityMetrics.MeterName);
        await using var scope = factory.Services.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<ICommandHandler<RebuildAvailability.Command, RebuildAvailability.Summary>>();

        // Act
        var result = await handler.HandleAsync(new RebuildAvailability.Command(), cancellationToken);

        // Assert
        Assert.True(result.IsSuccess);
        var duration = Assert.Single(recorder.Snapshot(), m => m.Instrument == AvailabilityMetrics.RebuildDurationInstrument);
        Assert.True(duration.Value >= 0);
    }
}
