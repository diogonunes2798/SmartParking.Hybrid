using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using SmartParking.Api.Tests.Fixtures;
using SmartParking.Application.Abstractions.Documents;
using static SmartParking.Api.Tests.Fixtures.AvailabilityView;

namespace SmartParking.Api.Tests.Features.Availability;

/// <summary>The index of the last event per spot (spec 004, AC-12), in an isolated MongoDB database.</summary>
[Collection(ApiCollectionDefinition.Name)]
public sealed class OccupancyEventIndexesTests(ApiFixture fixture)
{
    private static readonly BsonDocument LatestPerSpotKeys = new() { { "spotId", 1 }, { "timestamp", -1 }, { "_id", -1 } };

    [Fact]
    public async Task EnsureAsync_RunTwice_SingleIndex_AC12()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        var environment = await CreateIsolatedAsync(fixture, cancellationToken);
        await using var factory = ApiFixture.CreateFactory(environment.Api);
        var indexes = factory.Services.GetRequiredService<IOccupancyEventIndexes>();

        // Act
        await indexes.EnsureAsync(cancellationToken);
        await indexes.EnsureAsync(cancellationToken);

        // Assert
        var collection = new MongoClient(fixture.Databases.Mongo.GetConnectionString())
            .GetDatabase(environment.MongoDatabase)
            .GetCollection<BsonDocument>(IngestionApi.Collection);
        var listed = await (await collection.Indexes.ListAsync(cancellationToken)).ToListAsync(cancellationToken);
        Assert.Single(listed, index => index["key"].AsBsonDocument == LatestPerSpotKeys);
    }
}
