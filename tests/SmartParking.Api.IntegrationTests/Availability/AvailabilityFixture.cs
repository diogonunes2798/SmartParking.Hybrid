using System.Diagnostics.Metrics;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SmartParking.Api.IntegrationTests.Ingestion;
using SmartParking.Api.IntegrationTests.Relational;
using SmartParking.Application.Abstractions;
using SmartParking.Application.Availability;
using SmartParking.Application.Configuration;
using SmartParking.Application.Ingestion;
using SmartParking.Domain.Facilities;
using SmartParking.Infrastructure.Cache;
using SmartParking.Infrastructure.Cache.Availability;
using SmartParking.Infrastructure.Documents;
using SmartParking.Infrastructure.Messaging;
using SmartParking.Infrastructure.Relational;
using SmartParking.Infrastructure.Relational.Outbox;
using StackExchange.Redis;
using CacheRegistration = SmartParking.Infrastructure.Cache.DependencyInjection;
using DocumentsRegistration = SmartParking.Infrastructure.Documents.DependencyInjection;
using RelationalRegistration = SmartParking.Infrastructure.Relational.DependencyInjection;

namespace SmartParking.Api.IntegrationTests.Availability;

/// <summary>
/// Phase 4 (read model, UC-03): the API with PostgreSQL (migrated), MongoDB and Redis containers, plus helpers to
/// run the read model projectors like SmartParking.Workers does. Tests of this group run one at a time (one xUnit
/// collection), because the rebuild test clears the whole read model; every other test creates its own facilities
/// and spots (unique ids and zones), so it never depends on another test's data.
/// </summary>
public sealed class AvailabilityFixture : IAsyncLifetime
{
    private readonly ApiFixture _api = new();

    public HttpClient Client { get; private set; } = null!;

    public IServiceProvider Services => _api.Factory.Services;

    public IConnectionMultiplexer Redis { get; private set; } = null!;

    public IAvailabilityReadModel ReadModel => Services.GetRequiredService<IAvailabilityReadModel>();

    public async ValueTask InitializeAsync()
    {
        await _api.InitializeAsync();
        Client = _api.Factory.CreateClient();

        await using (var scope = Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<SmartParkingDbContext>().Database.MigrateAsync();
        }

        Redis = await ConnectionMultiplexer.ConnectAsync(_api.Redis.GetConnectionString() + ",allowAdmin=true"); // INFO in tests
    }

    public async ValueTask DisposeAsync()
    {
        Client?.Dispose();
        if (Redis is not null)
        {
            await Redis.DisposeAsync();
        }

        await _api.DisposeAsync();
    }

    public IDatabase Db => Redis.GetDatabase();

    /// <summary>A rebuilder wired like the Workers (PostgreSQL + MongoDB → Redis).</summary>
    public async Task<RebuildReport> RebuildAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        var rebuilder = ActivatorUtilities.CreateInstance<ReadModelRebuilder>(
            scope.ServiceProvider, new ReadModelMetrics(Services.GetRequiredService<IMeterFactory>()));
        return await rebuilder.RebuildAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Starts a host with the Workers' Phase 3 and 4 consumers (log writer, availability projector, configuration
    /// projector) on the real topics, with short poll intervals. The gate is open: the tests load what they need.
    /// </summary>
    public async Task<RunningConsumer> StartWorkersAsync()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"ConnectionStrings:{DocumentsRegistration.ConnectionStringName}"] = _api.Mongo.GetConnectionString(),
            [$"ConnectionStrings:{CacheRegistration.ConnectionStringName}"] = _api.Redis.GetConnectionString(),
        });

        builder.Services.AddMetrics();
        builder.Services.AddDocumentPersistence(builder.Configuration, []);
        builder.Services.AddCacheStore(builder.Configuration, []);
        builder.Services.AddSingleton<OccupancyIngestionMetrics>();
        builder.Services.AddSingleton<ReadModelMetrics>();
        builder.Services.AddSingleton(ReadModelGate.Opened());

        void Fast(RedisStreamConsumerOptions o, string stream, string group)
        {
            o.Stream = stream;
            o.Group = group;
            o.ConsumerName = "test-worker";
            o.PollInterval = TimeSpan.FromMilliseconds(20);
        }

        builder.Services.AddStreamConsumer<OccupancyLogWriter>(
            builder.Configuration, OccupancyLogConsumer.Name, o => Fast(o, OccupancyEventMessage.Topic, OccupancyLogConsumer.Name));
        builder.Services.AddStreamConsumer<AvailabilityProjector>(
            builder.Configuration, AvailabilityConsumers.Occupancy, o => Fast(o, OccupancyEventMessage.Topic, AvailabilityConsumers.Occupancy));
        builder.Services.AddStreamConsumer<ConfigurationProjector>(
            builder.Configuration, AvailabilityConsumers.Configuration, o => Fast(o, ConfigurationEventMessage.Topic, AvailabilityConsumers.Configuration));

        var host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        return new RunningConsumer(host);
    }

    // ---- Configuration (RF-06) through the API, as the operator would do it ----

    public async Task<FacilityDto> CreateFacilityAsync(string name, double latitude, double longitude, string zone)
    {
        var response = await Client.PostAsJsonAsync(
            "/api/facilities",
            new CreateFacility(name, new GeoJsonPoint("Point", [longitude, latitude]), 100, FacilityType.Uncovered, "CMC", zone),
            RelationalFixture.Json,
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<FacilityDto>(RelationalFixture.Json, TestContext.Current.CancellationToken))!;
    }

    public async Task<SpotDto> CreateSpotAsync(Guid facilityId, VehicleType type = VehicleType.Car)
    {
        var response = await Client.PostAsJsonAsync(
            $"/api/facilities/{facilityId}/spots",
            new CreateSpot($"S-{Guid.NewGuid():N}"[..12], type),
            RelationalFixture.Json,
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<SpotDto>(RelationalFixture.Json, TestContext.Current.CancellationToken))!;
    }

    /// <summary>The outbox rows of one aggregate, oldest first (what the relay of Phase 5 will publish).</summary>
    public async Task<List<OutboxMessage>> OutboxAsync(Guid aggregateId)
    {
        await using var scope = Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<SmartParkingDbContext>();
        return await context.Set<OutboxMessage>().AsNoTracking()
            .Where(m => m.AggregateId == aggregateId)
            .OrderBy(m => m.OccurredAt)
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Stand-in for the Phase 5 relay: publishes the given outbox rows, unchanged, on the configuration topic.
    /// </summary>
    public async Task RelayAsync(IEnumerable<OutboxMessage> rows)
    {
        var messages = rows.Select(r => new BusMessage(
                r.Id.ToString(),
                new ConfigurationEventMessage(r.Id, r.Type, r.AggregateId, r.OccurredAt, System.Text.Json.JsonDocument.Parse(r.Payload).RootElement)
                    .ToJson()))
            .ToList();
        await Services.GetRequiredService<IEventBus>().PublishAsync(
            ConfigurationEventMessage.Topic, messages, TestContext.Current.CancellationToken);
    }

    // ---- Direct read model setup (tests that exercise the read model without the bus) ----

    /// <summary>A facility with <paramref name="spots"/> car spots, loaded straight into the read model.</summary>
    public async Task<(Guid Facility, Guid[] Spots, string Zone)> SeedAsync(
        int spots, double latitude = 40.2033, double longitude = -8.4103, VehicleType type = VehicleType.Car)
    {
        var ct = TestContext.Current.CancellationToken;
        var facility = Guid.NewGuid();
        var zone = $"zone-{Guid.NewGuid():N}";
        var at = DateTimeOffset.UtcNow;
        await ReadModel.UpsertFacilityAsync(
            new FacilitySnapshot(facility, "Seeded", latitude, longitude, 100, FacilityType.Covered, zone), at, ct);
        var ids = new Guid[spots];
        for (var i = 0; i < spots; i++)
        {
            ids[i] = Guid.NewGuid();
            await ReadModel.UpsertSpotAsync(new SpotSnapshot(ids[i], facility, type), at, ct);
        }

        return (facility, ids, zone);
    }

    public async Task<Dictionary<string, long>> HashAsync(RedisKey key)
    {
        var entries = await Db.HashGetAllAsync(key);
        return entries.Where(e => long.TryParse(e.Value.ToString(), out _))
            .ToDictionary(e => e.Name.ToString(), e => (long)e.Value);
    }

    public Task<Dictionary<string, long>> FacilityCountersAsync(Guid facility) => HashAsync(ReadModelKeys.Facility(facility));

    public Task<Dictionary<string, long>> ZoneCountersAsync(string zone) => HashAsync(ReadModelKeys.Zone(zone));
}

[CollectionDefinition(Name)]
public sealed class AvailabilityTestGroup : ICollectionFixture<AvailabilityFixture>
{
    public const string Name = "Availability read model (PostgreSQL + MongoDB + Redis)";
}
