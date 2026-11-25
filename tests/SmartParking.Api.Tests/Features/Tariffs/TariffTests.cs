using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SmartParking.Api.Features.Tariffs;
using SmartParking.Api.Tests.Fixtures;

namespace SmartParking.Api.Tests.Features.Tariffs;

/// <summary>
/// Tariff versions (spec 001, ADR 0005): AC-7, AC-9 and the tariff cases of AC-17. A version in force is inserted
/// by SQL, as the owner, because the API only accepts versions that start now or later (plan 001b, point 8).
/// </summary>
[Collection(ApiCollectionDefinition.Name)]
public sealed class TariffTests(ApiFixture fixture)
{
    private string Owner => fixture.Databases.OwnerConnectionString;

    public static TheoryData<string> InvalidRequests => new() { "pricePerHour", "billingIncrementMinutes" };

    [Fact]
    public async Task Create_WithCurrentVersion_ClosesPreviousWithOneAuditOneEventSameTransaction_AC7()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = fixture.Hybrid.CreateClient();
        var (facility, _) = await FacilityApi.CreateAsync(client, cancellationToken);
        var currentId = await ConfigurationApi.SeedCurrentTariffAsync(Owner, facility.Id, cancellationToken);
        var validFrom = DateTimeOffset.UtcNow.AddHours(1);

        // Act
        using var response = await client.PostAsJsonAsync(
            ConfigurationApi.Tariffs(facility.Id), ConfigurationApi.NewTariff(validFrom), cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<TariffResponse>(cancellationToken);

        var closedAtNewStart = await DbProbe.ScalarAsync<bool>(
            Owner,
            "SELECT (SELECT valid_to FROM tariffs WHERE id = $1) = (SELECT valid_from FROM tariffs WHERE id = $2)",
            cancellationToken,
            currentId,
            created!.Id);
        Assert.True(closedAtNewStart);

        Assert.Equal(["Created|TariffVersionCreated.v1"], await FacilityApi.AuditTrailAsync(Owner, created.Id, cancellationToken));
        Assert.Empty(await FacilityApi.AuditTrailAsync(Owner, currentId, cancellationToken));
        Assert.Equal(["TariffVersionCreated.v1"], await ConfigurationApi.FacilityOutboxTypesAsync(Owner, facility.Id, cancellationToken));

        // Same xmin on the new version, the closed one, the audit entry and the outbox message: one transaction.
        var transactions = await DbProbe.ScalarAsync<long>(
            Owner,
            """
            SELECT count(DISTINCT x) FROM (
                SELECT xmin::text AS x FROM tariffs WHERE facility_id = $1
                UNION ALL SELECT xmin::text FROM audit_entries WHERE entity_id = $2
                UNION ALL SELECT xmin::text FROM outbox_messages WHERE payload->>'facilityId' = $1::text) rows
            """,
            cancellationToken,
            facility.Id,
            created.Id);
        Assert.Equal(1, transactions);

        using var payload = JsonDocument.Parse(await ConfigurationApi.PayloadAsync(Owner, created.Id, "TariffVersionCreated.v1", cancellationToken));
        Assert.Equal(currentId, payload.RootElement.GetProperty("previousVersionId").GetGuid());
    }

    [Fact]
    public async Task Create_ValidFromInPast_Returns400_AC9()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = fixture.Hybrid.CreateClient();
        var (facility, _) = await FacilityApi.CreateAsync(client, cancellationToken);

        // Act
        using var response = await client.PostAsJsonAsync(
            ConfigurationApi.Tariffs(facility.Id), ConfigurationApi.NewTariff(DateTimeOffset.UtcNow.AddHours(-1)), cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty("validFrom", out _));
        Assert.Empty(await ConfigurationApi.FacilityOutboxTypesAsync(Owner, facility.Id, cancellationToken));
    }

    [Fact]
    public async Task Delete_CurrentVersion_Returns409_AC9()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = fixture.Hybrid.CreateClient();
        var (facility, _) = await FacilityApi.CreateAsync(client, cancellationToken);
        var currentId = await ConfigurationApi.SeedCurrentTariffAsync(Owner, facility.Id, cancellationToken);
        var (_, etag) = await ConfigurationApi.GetAsync<TariffResponse>(client, $"{ConfigurationApi.Tariffs(facility.Id)}/{currentId}", cancellationToken);

        // Act
        using var response = await ConfigurationApi.SendAsync(
            client, HttpMethod.Delete, $"{ConfigurationApi.Tariffs(facility.Id)}/{currentId}", etag, cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var stillThere = await DbProbe.ScalarAsync<long>(Owner, "SELECT count(*) FROM tariffs WHERE id = $1", cancellationToken, currentId);
        Assert.Equal(1, stillThere);
    }

    [Fact]
    public async Task Delete_LastFutureVersion_Returns204AndReopensPrevious_AC9()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = fixture.Hybrid.CreateClient();
        var (facility, _) = await FacilityApi.CreateAsync(client, cancellationToken);
        var currentId = await ConfigurationApi.SeedCurrentTariffAsync(Owner, facility.Id, cancellationToken);
        var (future, etag) = await ConfigurationApi.PostAsync<TariffResponse>(
            client, ConfigurationApi.Tariffs(facility.Id), ConfigurationApi.NewTariff(DateTimeOffset.UtcNow.AddDays(1)), cancellationToken);

        // Act
        using var response = await ConfigurationApi.SendAsync(
            client, HttpMethod.Delete, $"{ConfigurationApi.Tariffs(facility.Id)}/{future.Id}", etag, cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var reopened = await DbProbe.ScalarAsync<bool>(Owner, "SELECT valid_to IS NULL FROM tariffs WHERE id = $1", cancellationToken, currentId);
        Assert.True(reopened);
        Assert.Equal(
            ["Created|TariffVersionCreated.v1", "Deleted|TariffVersionDeleted.v1"],
            await FacilityApi.AuditTrailAsync(Owner, future.Id, cancellationToken));

        var (current, _) = await ConfigurationApi.GetAsync<TariffResponse>(client, $"{ConfigurationApi.Tariffs(facility.Id)}/current", cancellationToken);
        Assert.Equal(currentId, current.Id);
    }

    [Fact]
    public async Task Create_NotAfterLatest_Returns409()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = fixture.Hybrid.CreateClient();
        var (facility, _) = await FacilityApi.CreateAsync(client, cancellationToken);
        var validFrom = DateTimeOffset.UtcNow.AddDays(2);
        await ConfigurationApi.PostAsync<TariffResponse>(client, ConfigurationApi.Tariffs(facility.Id), ConfigurationApi.NewTariff(validFrom), cancellationToken);

        // Act
        using var response = await client.PostAsJsonAsync(
            ConfigurationApi.Tariffs(facility.Id), ConfigurationApi.NewTariff(validFrom.AddHours(-1)), cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    /// <summary>
    /// Plan 001b, point 2. With the same start, whatever the interleaving, exactly one request wins: the others meet
    /// the rule «after the latest» (sequential), the latest version's xmin (412 turned into 409) or, without a previous
    /// version, the EXCLUDE constraint. None of them may surface as 412 or 500.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Create_ConcurrentSameStart_ExactlyOneCreatedOthers409(bool withCurrentVersion)
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = fixture.Hybrid.CreateClient();
        var (facility, _) = await FacilityApi.CreateAsync(client, cancellationToken);
        if (withCurrentVersion)
        {
            await ConfigurationApi.SeedCurrentTariffAsync(Owner, facility.Id, cancellationToken);
        }

        var request = ConfigurationApi.NewTariff(DateTimeOffset.UtcNow.AddDays(1));

        // Act
        var responses = await Task.WhenAll(Enumerable.Range(1, 4).Select(_ =>
            client.PostAsJsonAsync(ConfigurationApi.Tariffs(facility.Id), request, cancellationToken)));

        // Assert
        var codes = responses.Select(response => response.StatusCode).ToList();
        foreach (var response in responses)
        {
            response.Dispose();
        }

        Assert.Single(codes, code => code == HttpStatusCode.Created);
        Assert.Equal(3, codes.Count(code => code == HttpStatusCode.Conflict));
        var open = await DbProbe.ScalarAsync<long>(
            Owner, "SELECT count(*) FROM tariffs WHERE facility_id = $1 AND valid_to IS NULL", cancellationToken, facility.Id);
        Assert.Equal(1, open);
    }

    [Fact]
    public async Task Create_StartHalfMicrosecondAfterLatest_Returns409()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = fixture.Hybrid.CreateClient();
        var (facility, _) = await FacilityApi.CreateAsync(client, cancellationToken);
        var start = new DateTimeOffset(DateTimeOffset.UtcNow.AddDays(2).Date, TimeSpan.Zero);
        await ConfigurationApi.PostAsync<TariffResponse>(client, ConfigurationApi.Tariffs(facility.Id), ConfigurationApi.NewTariff(start), cancellationToken);

        // Act
        using var response = await client.PostAsJsonAsync(
            ConfigurationApi.Tariffs(facility.Id), ConfigurationApi.NewTariff(start.AddTicks(5)), cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(InvalidRequests))]
    public async Task Create_InvalidInput_Returns400ValidationProblemAndWritesNothing_AC17(string field)
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = fixture.Hybrid.CreateClient();
        var (facility, _) = await FacilityApi.CreateAsync(client, cancellationToken);
        var valid = ConfigurationApi.NewTariff(DateTimeOffset.UtcNow.AddDays(1));
        var request = field switch
        {
            "pricePerHour" => valid with { PricePerHour = -0.01m },
            "billingIncrementMinutes" => valid with { BillingIncrementMinutes = 0 },
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, "Unknown field."),
        };

        // Act
        using var response = await client.PostAsJsonAsync(ConfigurationApi.Tariffs(facility.Id), request, cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty(field, out _), $"No error for '{field}'.");
        var written = await DbProbe.ScalarAsync<long>(Owner, "SELECT count(*) FROM tariffs WHERE facility_id = $1", cancellationToken, facility.Id);
        Assert.Equal(0, written);
    }

    [Fact]
    public async Task GetCurrent_FacilityWithoutTariff_Returns404()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = fixture.Hybrid.CreateClient();
        var (facility, _) = await FacilityApi.CreateAsync(client, cancellationToken);

        // Act
        using var response = await client.GetAsync(new Uri($"{ConfigurationApi.Tariffs(facility.Id)}/current", UriKind.Relative), cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
