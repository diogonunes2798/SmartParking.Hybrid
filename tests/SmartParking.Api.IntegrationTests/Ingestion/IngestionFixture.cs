using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Bson;
using MongoDB.Driver;
using SmartParking.Application.Abstractions;
using SmartParking.Application.Ingestion;
using SmartParking.Infrastructure.Documents;
using SmartParking.Infrastructure.Documents.OccupancyEvents;
using SmartParking.Infrastructure.Messaging;
using StackExchange.Redis;
using Testcontainers.MongoDb;
using Testcontainers.Redis;
using DocumentsRegistration = SmartParking.Infrastructure.Documents.DependencyInjection;
using CacheRegistration = SmartParking.Infrastructure.Cache.DependencyInjection;
using RelationalRegistration = SmartParking.Infrastructure.Relational.DependencyInjection;

namespace SmartParking.Api.IntegrationTests.Ingestion;

/// <summary>
/// Phase 3 (UC-01 up to MongoDB): real Redis and MongoDB containers (same images as docker-compose.yml), the API
/// in memory, and consumer hosts started and stopped by each test (the Workers' registration, see
/// <see cref="StartConsumerAsync{THandler}"/>). PostgreSQL is not started: ingestion never touches it, and the API
/// only needs a connection string to start (Npgsql opens connections lazily).
/// </summary>
public sealed class IngestionFixture : IAsyncLifetime
{
    public MongoDbContainer Mongo { get; } = new MongoDbBuilder(ApiFixture.MongoImage).Build();

    public RedisContainer Redis { get; } = new RedisBuilder(ApiFixture.RedisImage).Build();

    public WebApplicationFactory<Program> Factory { get; private set; } = null!;

    public HttpClient Client { get; private set; } = null!;

    public IMongoCollection<BsonDocument> Events { get; private set; } = null!;

    public IConnectionMultiplexer RedisConnection { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(Mongo.StartAsync(), Redis.StartAsync());

        var mongo = Mongo.GetConnectionString();
        var redis = Redis.GetConnectionString();

        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(ApiFixture.EnvironmentName);
            builder.UseSetting(
                $"ConnectionStrings:{RelationalRegistration.ConnectionStringName}",
                "Host=postgres.invalid;Database=unused;Username=unused;Password=unused");
            builder.UseSetting($"ConnectionStrings:{DocumentsRegistration.ConnectionStringName}", mongo);
            builder.UseSetting($"ConnectionStrings:{CacheRegistration.ConnectionStringName}", redis);
        });
        Client = Factory.CreateClient();

        Events = new MongoClient(mongo)
            .GetDatabase(DocumentsRegistration.DefaultDatabaseName)
            .GetCollection<BsonDocument>(MongoOccupancyEventLog.CollectionName);
        RedisConnection = await ConnectionMultiplexer.ConnectAsync(redis);
    }

    /// <summary>
    /// Starts a consumer host wired like SmartParking.Workers (Program.cs), with <typeparamref name="THandler"/> as
    /// the batch handler. <paramref name="configure"/> sets stream, group, consumer name and intervals.
    /// </summary>
    public async Task<RunningConsumer> StartConsumerAsync<THandler>(
        Action<RedisStreamConsumerOptions> configure,
        Action<IServiceCollection>? services = null,
        string? redisConnectionString = null)
        where THandler : class, IMessageBatchHandler
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"ConnectionStrings:{DocumentsRegistration.ConnectionStringName}"] = Mongo.GetConnectionString(),
            [$"ConnectionStrings:{CacheRegistration.ConnectionStringName}"] = redisConnectionString ?? Redis.GetConnectionString(),
        });

        builder.Services.AddMetrics();
        builder.Services.AddDocumentPersistence(builder.Configuration, []);
        builder.Services.AddSingleton<OccupancyIngestionMetrics>();
        builder.Services.AddScoped<OccupancyLogWriter>();
        services?.Invoke(builder.Services);
        builder.Services.AddStreamConsumer<THandler>(builder.Configuration, "test", configure);

        var host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        return new RunningConsumer(host);
    }

    /// <summary>Standard consumer of the API's stream (as in Workers), with short intervals for tests.</summary>
    public Task<RunningConsumer> StartOccupancyLogConsumerAsync(string consumerName = "test-consumer") =>
        StartConsumerAsync<OccupancyLogWriter>(o =>
        {
            o.Stream = OccupancyEventMessage.Topic;
            o.Group = OccupancyLogConsumer.Name;
            o.ConsumerName = consumerName;
            o.PollInterval = TimeSpan.FromMilliseconds(20);
        });

    public Task<long> CountAsync(Guid eventId) =>
        Events.CountDocumentsAsync(
            new BsonDocument("eventId", new BsonBinaryData(eventId, GuidRepresentation.Standard)),
            cancellationToken: TestContext.Current.CancellationToken);

    public Task<BsonDocument> FindAsync(Guid eventId) =>
        Events.Find(new BsonDocument("eventId", new BsonBinaryData(eventId, GuidRepresentation.Standard)))
            .SingleAsync(TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync()
    {
        Client?.Dispose();
        if (RedisConnection is not null)
        {
            await RedisConnection.DisposeAsync();
        }

        if (Factory is not null)
        {
            await Factory.DisposeAsync();
        }

        await Task.WhenAll(Mongo.DisposeAsync().AsTask(), Redis.DisposeAsync().AsTask());
    }

    /// <summary>Polls <paramref name="condition"/> until it holds or 15 s elapse.</summary>
    public static async Task EventuallyAsync(Func<Task<bool>> condition, string because)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting: " + because);
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>A valid event as the sensors send it (spec §3), with an optional payload.</summary>
    public static Dictionary<string, object?> Event(Guid? eventId = null, object? payload = null, string timestamp = "2026-11-14T10:00:00Z") => new()
    {
        ["eventId"] = (eventId ?? Guid.NewGuid()).ToString(),
        ["sensorId"] = Guid.NewGuid().ToString(),
        ["spotId"] = Guid.NewGuid().ToString(),
        ["facilityId"] = Guid.NewGuid().ToString(),
        ["type"] = "OCCUPIED",
        ["timestamp"] = timestamp,
        ["payload"] = payload ?? new Dictionary<string, object?>(),
    };

    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web);
}

/// <summary>A started consumer host; disposing it stops it gracefully (cancels the handler, no XACK).</summary>
public sealed class RunningConsumer(IHost host) : IAsyncDisposable
{
    private bool stopped;

    public async Task StopAsync()
    {
        if (!stopped)
        {
            stopped = true;
            await host.StopAsync(CancellationToken.None);
            host.Dispose();
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}

[CollectionDefinition(Name)]
public sealed class IngestionTestGroup : ICollectionFixture<IngestionFixture>
{
    public const string Name = "Ingestion (Redis + MongoDB)";
}
