using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using SmartParking.Api.Common;
using SmartParking.Api.Features.Facilities;
using SmartParking.Api.Features.Sensors;
using SmartParking.Api.Features.Spots;
using SmartParking.Api.Features.Zones;
using SmartParking.Api.Tests.Fixtures;
using SmartParking.SensorSimulator;
using SmartParking.SensorSimulator.Generation;
using SmartParking.SensorSimulator.Provisioning;
using SmartParking.SensorSimulator.Sending;
using static SmartParking.Api.Tests.Fixtures.IngestionApi;

namespace SmartParking.Api.Tests.Features.Simulator;

/// <summary>
/// The simulator against the test Api (spec 003, F4b): <c>--provision</c> through the F2 endpoints (AC-19), a real-time
/// run of one gateway (AC-23) and the resend after a 503 (AC-24).
/// </summary>
[Collection(ApiCollectionDefinition.Name)]
public sealed class SimulatorApiTests(ApiFixture fixture)
{
    /// <summary>
    /// Tolerance of the AC-24 wait check: the default Windows timer tick (15.625 ms). <c>Task.Delay</c> follows the
    /// system tick count, which can run up to one tick ahead of <see cref="Stopwatch"/>. A correctness margin, not a
    /// performance value.
    /// </summary>
    private static readonly TimeSpan TimerResolution = TimeSpan.FromMilliseconds(16);

    private string Mongo => fixture.Databases.Mongo.GetConnectionString();

    [Fact]
    public async Task Provision_Spots20Seed42_RegistersFacilityZonesSpotsAndSensors_AC19()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        var api = fixture.Hybrid;
        var mappingFile = Path.Combine(Path.GetTempPath(), $"sensor-map-{Guid.NewGuid():N}.json");
        var command = SimulatorCommand.Create(services => SimulatorHost.UseTestServer(services, api));

        try
        {
            // Act: the command line of the spec (5 zones by default).
            var exitCode = await command
                .Parse(["--provision", "--spots", "20", "--seed", "42", "--mapping", mappingFile, "--api", api.Server.BaseAddress.ToString()])
                .InvokeAsync(cancellationToken: cancellationToken);

            // Assert
            Assert.Equal(ExitCodes.Success, exitCode);
            var map = await SensorMap.LoadAsync(mappingFile, cancellationToken);
            Assert.NotNull(map);
            Assert.Equal(42, map.Seed);
            Assert.Equal(20, map.Sensors.Count);
            Assert.Equal(20, map.Sensors.Select(sensor => sensor.HardwareId).Distinct().Count());
            Assert.Equal(20, map.Sensors.Select(sensor => sensor.SpotId).Distinct().Count());

            using var client = api.CreateClient();
            var facility = await client.GetFromJsonAsync<FacilityResponse>($"{FacilityApi.Route}/{map.FacilityId}", cancellationToken);
            Assert.Equal(Provisioner.FacilityName(42), facility!.Name);

            var zones = await client.GetFromJsonAsync<PagedResponse<ZoneResponse>>($"{ConfigurationApi.Zones(map.FacilityId)}?pageSize=100", cancellationToken);
            Assert.Equal(5, zones!.TotalCount);
            Assert.Equal(map.ZoneIds.Order(), zones.Items.Select(zone => zone.Id).Order());

            var spots = await client.GetFromJsonAsync<PagedResponse<SpotResponse>>($"{ConfigurationApi.Spots(map.FacilityId)}?pageSize=100", cancellationToken);
            Assert.Equal(20, spots!.TotalCount);
            Assert.All(spots.Items, spot => Assert.Equal("Active", spot.Status));
            Assert.Equal(4, spots.Items.GroupBy(spot => spot.ZoneId).Select(group => group.Count()).Distinct().Single());

            var sensors = await client.GetFromJsonAsync<PagedResponse<SensorResponse>>(
                $"{ConfigurationApi.SensorsRoute}?facilityId={map.FacilityId}&pageSize=100", cancellationToken);
            Assert.Equal(20, sensors!.TotalCount);
            Assert.All(sensors.Items, sensor =>
            {
                Assert.Equal("Occupancy", sensor.Kind);
                Assert.Equal("Active", sensor.Status);
            });
            Assert.Equal(
                map.Sensors.Select(sensor => (sensor.HardwareId, (Guid?)sensor.SpotId, (Guid?)sensor.ZoneId)).Order(),
                sensors.Items.Select(sensor => (sensor.HardwareId, sensor.SpotId, sensor.ZoneId)).Order());
        }
        finally
        {
            File.Delete(mappingFile);
        }
    }

    [Fact]
    public async Task Gateway_RealTimeRun_NoRequestAbove500AndEveryEventAccepted_AC23()
    {
        // Arrange: 20 spots at 50 events/s each, so the gateway fills batches of 500 before the 1 s flush.
        var cancellationToken = TestContext.Current.CancellationToken;
        var api = fixture.Hybrid;
        using var client = api.CreateClient();
        var site = await CreateSiteAsync(client, 20, cancellationToken);
        var recorder = new RecordingHandler();
        await using var services = SimulatorHost.Create(api, recorder);
        var generator = new GeneratorSettings(42, TimeSpan.FromMilliseconds(20), 10, []);

        // Act
        var summary = await services.GetRequiredService<GatewayRunner>().RunAsync(
            SimulatorMaps.FromSite(site), generator, SenderSettings.Default, TimeSpan.FromSeconds(1.5), cancellationToken);

        // Assert
        var sizes = recorder.Requests.Select(request => request.EventIds.Count).ToList();
        Assert.All(sizes, size => Assert.InRange(size, 1, SenderSettings.Default.BatchSize));
        Assert.Contains(SenderSettings.Default.BatchSize, sizes);
        Assert.Equal(summary.Generated, sizes.Sum());
        Assert.Equal(summary.Generated, summary.Accepted);
        Assert.Equal(0, summary.NotDeliveredEvents);
        Assert.Equal(summary.Generated, (await FindAsync(Mongo, recorder.Requests.SelectMany(request => request.EventIds), cancellationToken)).Count);
    }

    [Fact]
    public async Task Send_ApiReturns503Once_WaitsRetryAfterAndResendsSameEventIds_AC24()
    {
        // Arrange: the event log is unreachable on the first append only; the Api answers 503 with Retry-After: 1.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var api = fixture.CreateFactory(configureServices: FailOnceEventLog.Register);
        using var client = api.CreateClient();
        var site = await CreateSiteAsync(client, 3, cancellationToken);
        var recorder = new RecordingHandler();
        await using var services = SimulatorHost.Create(api, recorder);
        var generator = new GeneratorSettings(42, TimeSpan.FromSeconds(10), 10, []);
        var batch = OccupancyGenerator.Generate(SimulatorMaps.FromSite(site).Sensors, generator, TimeProvider.System.GetUtcNow().AddHours(-1)).Take(6).ToList();

        // Act
        var outcome = await services.GetRequiredService<IngestionSender>().SendAsync(batch, maxRetries: 3, cancellationToken);

        // Assert
        Assert.True(outcome.Delivered);
        Assert.Equal(1, outcome.Retries);
        Assert.Equal(batch.Count, outcome.Accepted);

        var requests = recorder.Requests;
        Assert.Equal([HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK], requests.Select(request => request.StatusCode));
        Assert.Equal(TimeSpan.FromSeconds(1), requests[0].RetryAfter);
        Assert.Equal(batch.Select(item => item.EventId), requests[0].EventIds);
        Assert.Equal(requests[0].EventIds, requests[1].EventIds);
        // The wait counts from the arrival of the 503, not from the first request (review of F4b, finding 3).
        var waited = Stopwatch.GetElapsedTime(requests[0].AnsweredAt, requests[1].Timestamp);
        Assert.True(waited >= requests[0].RetryAfter - TimerResolution, $"Resent after {waited}, Retry-After {requests[0].RetryAfter}.");

        var stored = await FindAsync(Mongo, batch.Select(item => item.EventId), cancellationToken);
        Assert.Equal(batch.Count, stored.Count);
    }

    [Fact]
    public async Task Gateway_OkWithUnreadableBody_KeepsSendingAndCountsBatchesAsNotDelivered_AC23()
    {
        // Arrange: every 200 arrives with truncated JSON (review of F4b, finding 1).
        var cancellationToken = TestContext.Current.CancellationToken;
        var api = fixture.Hybrid;
        using var client = api.CreateClient();
        var site = await CreateSiteAsync(client, 5, cancellationToken);
        var corrupter = new TruncatedOkBodyHandler();
        await using var services = SimulatorHost.Create(api, corrupter);
        var generator = new GeneratorSettings(42, TimeSpan.FromMilliseconds(50), 10, []);
        var settings = SenderSettings.Default with { BatchSize = 20 };

        // Act
        var summary = await services.GetRequiredService<GatewayRunner>().RunAsync(
            SimulatorMaps.FromSite(site), generator, settings, TimeSpan.FromSeconds(1), cancellationToken);

        // Assert: the run ends with its summary, every batch was sent, and none is counted as delivered.
        Assert.True(summary.Batches > 1);
        Assert.Equal(summary.Batches, corrupter.Requests);
        Assert.Equal(summary.Generated, summary.NotDeliveredEvents);
        Assert.Equal(0, summary.Accepted);
    }

    [Fact]
    public async Task Send_ApiReturns503Always_StopsAfterMaxRetries_AC24()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var api = fixture.CreateFactory(configureServices: services =>
            services.AddSingleton<SmartParking.Application.Abstractions.Documents.IOccupancyEventLog, UnavailableEventLog>());
        using var client = api.CreateClient();
        var site = await CreateSiteAsync(client, 1, cancellationToken);
        var recorder = new RecordingHandler();
        await using var services = SimulatorHost.Create(api, recorder);
        var generator = new GeneratorSettings(42, TimeSpan.FromSeconds(10), 10, []);
        var batch = OccupancyGenerator.Generate(SimulatorMaps.FromSite(site).Sensors, generator, TimeProvider.System.GetUtcNow().AddHours(-1)).Take(2).ToList();

        // Act
        var outcome = await services.GetRequiredService<IngestionSender>().SendAsync(batch, maxRetries: 1, cancellationToken);

        // Assert
        Assert.False(outcome.Delivered);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, outcome.StatusCode);
        Assert.Equal(1, outcome.Retries);
        Assert.Equal(2, recorder.Requests.Count);
        Assert.All(recorder.Requests, request => Assert.Equal(batch.Select(item => item.EventId), request.EventIds));
    }
}
