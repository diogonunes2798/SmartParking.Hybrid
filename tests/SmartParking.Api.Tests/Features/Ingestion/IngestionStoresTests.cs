using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Npgsql;
using SmartParking.Application.Abstractions.Documents;
using SmartParking.Api.Tests.Fixtures;
using SmartParking.Infrastructure;
using SmartParking.Infrastructure.Documents;
using SmartParking.Infrastructure.Relational;
using SmartParking.Tests.Common;
using Testcontainers.MongoDb;
using static SmartParking.Api.Tests.Fixtures.IngestionApi;

namespace SmartParking.Api.Tests.Features.Ingestion;

/// <summary>
/// How the ingestion uses the stores (spec 003): one registry query per batch, 503 when a store is unreachable,
/// durable writes, and 501 in Monolithic mode (AC-11, AC-12, AC-13, AC-14, AC-16).
/// </summary>
[Collection(ApiCollectionDefinition.Name)]
public sealed class IngestionStoresTests(ApiFixture fixture)
{
    /// <summary>Past events are accepted without limit, so the real clock of these hosts does not matter.</summary>
    private static readonly DateTimeOffset Past = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    private string Mongo => fixture.Databases.Mongo.GetConnectionString();

    [Fact]
    public async Task Ingest_50EventsFrom50Sensors_RunsExactlyOneSqlCommand_AC11()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        var interceptor = new CountingCommandInterceptor();
        await using var factory = fixture.CreateFactory(configureServices: services =>
            services.ConfigureDbContext<SmartParkingDbContext>(options => options.AddInterceptors(interceptor)));
        using var client = factory.CreateClient();
        var site = await CreateSiteAsync(client, 50, cancellationToken);
        var now = Past;
        var batch = Batch([.. site.Spots.Select(spot => Event(spot, now))]);
        interceptor.Reset();

        // Act
        var outcome = await PostOkAsync(client, batch, cancellationToken);

        // Assert
        Assert.Equal(50, outcome.Accepted);
        Assert.Equal(1, interceptor.Count);
    }

    [Fact]
    public async Task Ingest_MongoStopped_Returns503WithRetryAfterAndSameBatchIsAcceptedAfterRestart_AC12()
    {
        // Arrange: a MongoDB of its own on a fixed host port, so the Api reconnects to it after the restart.
        var cancellationToken = TestContext.Current.CancellationToken;
        var port = FreePort();
        await using var mongo = new MongoDbBuilder(ContainerImages.Mongo).WithReplicaSet().WithPortBinding(port, MongoDbBuilder.MongoDbPort).Build();
        await mongo.StartAsync(cancellationToken);
        var settings = fixture.Databases.Settings(PersistenceMode.Hybrid);
        settings["ConnectionStrings:Mongo"] = WithShortTimeouts(mongo.GetConnectionString());
        await using var factory = ApiFixture.CreateFactory(settings);
        using var client = factory.CreateClient();
        var site = await CreateSiteAsync(client, 2, cancellationToken);
        var now = Past;
        var batch = Batch([.. site.Spots.Select(spot => Event(spot, now))]);
        await mongo.StopAsync(cancellationToken);

        // Act
        using var unavailable = await PostAsync(client, batch, cancellationToken);
        await mongo.StartAsync(cancellationToken);
        var outcome = await PostOkAsync(client, batch, cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
        Assert.Equal("application/problem+json", unavailable.Content.Headers.ContentType?.MediaType);
        Assert.Equal(TimeSpan.FromSeconds(1), unavailable.Headers.RetryAfter?.Delta);
        Assert.Equal(2, outcome.Accepted + outcome.Duplicates);
        Assert.Equal(2, (await FindAsync(mongo.GetConnectionString(), batch["events"]!.AsArray().Select(node => IdOf(node!.AsObject())), cancellationToken)).Count);
    }

    [Fact]
    public async Task Ingest_PostgresUnreachable_Returns503WithRetryAfterAndStoresNothing_AC13()
    {
        // Arrange: the site is registered through the shared host; the batch goes to a host whose PostgreSQL is a closed port.
        var cancellationToken = TestContext.Current.CancellationToken;
        using var registry = fixture.Hybrid.CreateClient();
        var site = await CreateSiteAsync(registry, 2, cancellationToken);
        var settings = fixture.Databases.Settings(PersistenceMode.Hybrid);
        settings["ConnectionStrings:Postgres"] = DatabaseContainers.UnreachableSettings(PersistenceMode.Hybrid)["ConnectionStrings:Postgres"];
        await using var factory = ApiFixture.CreateFactory(settings);
        using var client = factory.CreateClient();
        var now = Past;
        JsonObject[] events = [.. site.Spots.Select(spot => Event(spot, now))];

        // Act
        using var response = await PostAsync(client, Batch(events), cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(TimeSpan.FromSeconds(1), response.Headers.RetryAfter?.Delta);
        Assert.Empty(await FindAsync(Mongo, events.Select(IdOf), cancellationToken));
    }

    [Fact]
    public async Task Ingest_PostgresLoginRejected_Returns500WithoutRetryAfter_AC13()
    {
        // Arrange: a configuration fault (wrong password, SQLSTATE 28P01) is not transient, so the gateway must not be
        // told to retry it (review of F4a, finding 2).
        var cancellationToken = TestContext.Current.CancellationToken;
        using var registry = fixture.Hybrid.CreateClient();
        var site = await CreateSiteAsync(registry, 1, cancellationToken);
        var settings = fixture.Databases.Settings(PersistenceMode.Hybrid);
        settings["ConnectionStrings:Postgres"] = new NpgsqlConnectionStringBuilder(fixture.Databases.AppConnectionString) { Password = "wrong" }.ConnectionString;
        await using var factory = ApiFixture.CreateFactory(settings);
        using var client = factory.CreateClient();
        var occupancyEvent = Event(site.Spots[0], Past);

        // Act
        using var response = await PostAsync(client, Batch(occupancyEvent), cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Null(response.Headers.RetryAfter);
        Assert.Empty(await FindAsync(Mongo, [IdOf(occupancyEvent)], cancellationToken));
    }

    [Fact]
    public async Task Ingest_EventLogUnavailable_RecordsNoOutcomeMetrics_AC17()
    {
        // Arrange: the batch has a rejected and a valid event and ends in 503, so the gateway will send it again.
        // Counting it now would count its events twice (review of F4a, finding 3).
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = fixture.CreateFactory(configureServices: services =>
            services.AddSingleton<IOccupancyEventLog, UnavailableEventLog>());
        using var client = factory.CreateClient();
        var site = await CreateSiteAsync(client, 1, cancellationToken);
        using var recorder = new IngestionMeterRecorder(factory.Services);
        var valid = Event(site.Spots[0], Past);
        var unknown = Event(ConfigurationApi.NewHardwareId(), site.Spots[0].SpotId, Past);

        // Act
        using var response = await PostAsync(client, Batch(unknown, valid), cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var recorded = recorder.Snapshot();
        Assert.DoesNotContain(recorded, m => m.Instrument is "ingestion.events" or "ingestion.batch.size");
        Assert.Single(recorded, m => m.Instrument == "ingestion.append.duration");
    }

    [Fact]
    public void Collection_HybridMode_WritesWithMajorityAndJournal_AC14()
    {
        // Arrange
        var collection = fixture.Hybrid.Services.GetRequiredService<OccupancyEventsCollection>().Collection;

        // Act
        var writeConcern = collection.Settings.WriteConcern;

        // Assert
        Assert.Equal(OccupancyEventsCollection.Name, collection.CollectionNamespace.CollectionName);
        Assert.Equal(WriteConcern.WMajority.W, writeConcern.W);
        Assert.True(writeConcern.Journal);
    }

    [Fact]
    public async Task Ingest_MonolithicMode_Returns501ProblemDetails_AC16()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = fixture.CreateFactory(PersistenceMode.Monolithic);
        using var client = factory.CreateClient();
        var site = await CreateSiteAsync(client, 1, cancellationToken);
        var occupancyEvent = Event(site.Spots[0], Past);

        // Act
        using var response = await PostAsync(client, Batch(occupancyEvent), cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        Assert.Equal(501, problem.RootElement.GetProperty("status").GetInt32());
        Assert.Empty(await FindAsync(Mongo, [IdOf(occupancyEvent)], cancellationToken));
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string WithShortTimeouts(string connectionString) =>
        connectionString + (connectionString.Contains('?', StringComparison.Ordinal) ? "&" : "?")
        + "serverSelectionTimeoutMS=2000&connectTimeoutMS=2000&socketTimeoutMS=5000";
}
