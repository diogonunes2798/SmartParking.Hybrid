using System.Net;
using System.Net.Http.Json;
using SmartParking.Api.Tests.Fixtures;

namespace SmartParking.Api.Tests.Features.Tariffs;

/// <summary>
/// AC-8: when the outbox insert fails, the business change and the audit entry are rolled back with it (one
/// transaction, no 2PC). The failing trigger lives in an isolated database, so it cannot break the other tests.
/// </summary>
[Collection(ApiCollectionDefinition.Name)]
public sealed class TariffAtomicityTests(ApiFixture fixture)
{
    [Fact]
    public async Task Create_OutboxInsertFails_Returns500AndWritesNothing_AC8()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        var database = await fixture.Databases.CreateIsolatedDatabaseAsync(cancellationToken);
        await using var factory = fixture.CreateFactory(database);
        using var client = factory.CreateClient();
        var owner = database.OwnerConnectionString;
        var (facility, _) = await FacilityApi.CreateAsync(client, cancellationToken);
        var currentId = await ConfigurationApi.SeedCurrentTariffAsync(owner, facility.Id, cancellationToken);
        await DbProbe.ExecuteAsync(
            owner,
            """
            CREATE FUNCTION fail_outbox_insert() RETURNS trigger LANGUAGE plpgsql AS
            'BEGIN RAISE EXCEPTION ''forced outbox failure (AC-8)''; END';
            CREATE TRIGGER outbox_insert_fails BEFORE INSERT ON outbox_messages
                FOR EACH ROW EXECUTE FUNCTION fail_outbox_insert();
            """,
            cancellationToken);
        var auditBefore = await DbProbe.ScalarAsync<long>(owner, "SELECT count(*) FROM audit_entries", cancellationToken);

        // Act
        using var response = await client.PostAsJsonAsync(
            ConfigurationApi.Tariffs(facility.Id), ConfigurationApi.NewTariff(DateTimeOffset.UtcNow.AddHours(1)), cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(1, await DbProbe.ScalarAsync<long>(owner, "SELECT count(*) FROM tariffs WHERE facility_id = $1", cancellationToken, facility.Id));
        Assert.True(await DbProbe.ScalarAsync<bool>(owner, "SELECT valid_to IS NULL FROM tariffs WHERE id = $1", cancellationToken, currentId));
        Assert.Equal(auditBefore, await DbProbe.ScalarAsync<long>(owner, "SELECT count(*) FROM audit_entries", cancellationToken));
    }
}
