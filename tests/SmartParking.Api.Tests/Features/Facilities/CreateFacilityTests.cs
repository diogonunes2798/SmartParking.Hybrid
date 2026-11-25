using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SmartParking.Api.Features.Facilities;
using SmartParking.Api.Tests.Fixtures;

namespace SmartParking.Api.Tests.Features.Facilities;

[Collection(ApiCollectionDefinition.Name)]
public sealed class CreateFacilityTests(ApiFixture fixture)
{
    private string Owner => fixture.Databases.OwnerConnectionString;

    [Fact]
    public async Task CreateThenGet_ValidCoordinates_ReturnsSameCoordinates_AC2()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = fixture.Hybrid.CreateClient();
        var request = FacilityApi.NewRequest() with { Latitude = 41.1496, Longitude = -8.6109 };
        var (created, _) = await FacilityApi.CreateAsync(client, cancellationToken, request);

        // Act
        var facility = await client.GetFromJsonAsync<FacilityResponse>($"{FacilityApi.Route}/{created.Id}", cancellationToken);

        // Assert
        Assert.Equal(41.1496, facility!.Latitude, 6);
        Assert.Equal(-8.6109, facility.Longitude, 6);
    }

    [Fact]
    public async Task Create_ValidRequest_Returns201WithOneAuditEntryAndOneOutboxMessage_AC4()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = fixture.Hybrid.CreateClient();

        // Act
        using var response = await client.PostAsJsonAsync(FacilityApi.Route, FacilityApi.NewRequest(), cancellationToken);
        var facility = await response.Content.ReadFromJsonAsync<FacilityResponse>(cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal($"/api/facilities/{facility!.Id}", response.Headers.Location?.AbsolutePath);
        Assert.False(response.Headers.ETag!.IsWeak);

        Assert.Equal(["Created|FacilityCreated.v1"], await FacilityApi.AuditTrailAsync(Owner, facility.Id, cancellationToken));

        var audit = await DbProbe.ScalarAsync<string>(
            Owner,
            """
            SELECT json_build_object('actor', actor, 'before', before, 'after', after, 'payload', o.payload, 'published', o.published_at)::text
            FROM audit_entries a JOIN outbox_messages o ON o.id = a.outbox_message_id
            WHERE a.entity_id = $1
            """,
            cancellationToken,
            facility.Id);
        using var row = JsonDocument.Parse(audit);
        Assert.Equal("anonymous", row.RootElement.GetProperty("actor").GetString());
        Assert.Equal(JsonValueKind.Null, row.RootElement.GetProperty("before").ValueKind);
        Assert.Equal(facility.Name, row.RootElement.GetProperty("after").GetProperty("name").GetString());
        Assert.Equal(facility.Id, row.RootElement.GetProperty("payload").GetProperty("id").GetGuid());
        Assert.Equal(JsonValueKind.Null, row.RootElement.GetProperty("published").ValueKind);
    }

    [Fact]
    public async Task Create_RepeatedNameForOperator_Returns409AndWritesNothing()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = fixture.Hybrid.CreateClient();
        var (first, _) = await FacilityApi.CreateAsync(client, cancellationToken);

        // Act
        using var response = await client.PostAsJsonAsync(FacilityApi.Route, FacilityApi.NewRequest(first.Name), cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var facilities = await DbProbe.ScalarAsync<long>(
            Owner, "SELECT count(*) FROM parking_facilities WHERE name = $1", cancellationToken, first.Name);
        var audits = await DbProbe.ScalarAsync<long>(
            Owner, "SELECT count(*) FROM audit_entries WHERE after->>'name' = $1", cancellationToken, first.Name);
        var messages = await DbProbe.ScalarAsync<long>(
            Owner, "SELECT count(*) FROM outbox_messages WHERE payload->>'name' = $1", cancellationToken, first.Name);
        Assert.Equal(1, facilities);
        Assert.Equal(1, audits);
        Assert.Equal(1, messages);
    }

    [Fact]
    public async Task Create_UnknownOperator_Returns409()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = fixture.Hybrid.CreateClient();
        var request = FacilityApi.NewRequest() with { OperatorId = Guid.CreateVersion7() };

        // Act
        using var response = await client.PostAsJsonAsync(FacilityApi.Route, request, cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }
}
