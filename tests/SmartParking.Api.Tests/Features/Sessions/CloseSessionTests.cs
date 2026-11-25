using System.Net;
using System.Net.Http.Json;
using SmartParking.Api.Features.Sessions;
using SmartParking.Api.Features.Tariffs;
using SmartParking.Api.Tests.Fixtures;

namespace SmartParking.Api.Tests.Features.Sessions;

/// <summary>AC-9 and AC-10 (spec 002): closing a session and pricing it with its own tariff version.</summary>
[Collection(ApiCollectionDefinition.Name)]
public sealed class CloseSessionTests(ApiFixture fixture)
{
    private static readonly DateTimeOffset Wednesday10 = new(2026, 12, 2, 10, 0, 0, TimeSpan.Zero);

    private string App => fixture.Databases.AppConnectionString;

    [Fact]
    public async Task Close_After40Minutes_Returns200Amount090OneAuditOneEvent_AC9()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        fixture.Clock.SetUtcNow(Wednesday10);
        using var client = fixture.Sessions.CreateClient();
        var arranged = await SessionApi.ArrangeAsync(client, fixture.Clock, cancellationToken);
        var opened = await SessionApi.OpenOkAsync(client, arranged.SpotId, arranged.VehicleId, cancellationToken);
        fixture.Clock.Advance(TimeSpan.FromMinutes(40));

        // Act
        using var response = await SessionApi.CloseAsync(client, opened.Id, cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var closed = (await response.Content.ReadFromJsonAsync<SessionResponse>(cancellationToken))!;
        Assert.Equal("Closed", closed.Status);
        Assert.Equal(Wednesday10.AddMinutes(40), closed.EndedAt);
        Assert.Equal(0.90m, closed.Amount);
        Assert.Equal(
            ["ParkingSession|Created|SessionOpened.v1", "ParkingSession|Updated|SessionClosed.v1"],
            await UserApi.AuditAsync(App, opened.Id, cancellationToken));
        Assert.Equal(["SessionOpened.v1", "SessionClosed.v1"], await SessionApi.SessionOutboxTypesAsync(App, opened.Id, cancellationToken));
    }

    [Fact]
    public async Task Close_AlreadyClosed_Returns409_AC9()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        fixture.Clock.SetUtcNow(Wednesday10);
        using var client = fixture.Sessions.CreateClient();
        var arranged = await SessionApi.ArrangeAsync(client, fixture.Clock, cancellationToken);
        var closed = await SessionApi.ClosedSessionAsync(client, fixture.Clock, arranged, TimeSpan.FromMinutes(40), cancellationToken);

        // Act
        using var response = await SessionApi.CloseAsync(client, closed.Id, cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(["SessionOpened.v1", "SessionClosed.v1"], await SessionApi.SessionOutboxTypesAsync(App, closed.Id, cancellationToken));
    }

    [Fact]
    public async Task Close_NewVersionDuringSession_UsesSessionVersion_AC10()
    {
        // Arrange: V1 (1.20 €/h) in force at the opening; V2 (2.40 €/h) starts one hour later.
        var cancellationToken = TestContext.Current.CancellationToken;
        fixture.Clock.SetUtcNow(Wednesday10);
        using var client = fixture.Sessions.CreateClient();
        var arranged = await SessionApi.ArrangeAsync(client, fixture.Clock, cancellationToken);
        var opened = await SessionApi.OpenOkAsync(client, arranged.SpotId, arranged.VehicleId, cancellationToken);
        var (v2, _) = await ConfigurationApi.PostAsync<TariffResponse>(
            client, ConfigurationApi.Tariffs(arranged.Facility.Id), SessionApi.ReferenceTariff(Wednesday10.AddHours(1), 2.40m), cancellationToken);
        fixture.Clock.Advance(TimeSpan.FromHours(2));

        // Act
        var closed = await SessionApi.CloseOkAsync(client, opened.Id, cancellationToken);

        // Assert: 2 h = 8 blocks of 15 min; 0.30 € each with V1 (V2 would give 4.80 €).
        Assert.NotEqual(arranged.TariffId, v2.Id);
        Assert.Equal(arranged.TariffId, closed.TariffId);
        Assert.Equal(2.40m, closed.Amount);
    }

    [Fact]
    public async Task Close_31Hours_AppliesDailyMaximum()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        fixture.Clock.SetUtcNow(Wednesday10);
        using var client = fixture.Sessions.CreateClient();
        var arranged = await SessionApi.ArrangeAsync(client, fixture.Clock, cancellationToken);

        // Act (closing outside the opening hours is allowed)
        var closed = await SessionApi.ClosedSessionAsync(client, fixture.Clock, arranged, TimeSpan.FromHours(31), cancellationToken);

        // Assert
        Assert.Equal(18.40m, closed.Amount);
    }

    [Fact]
    public async Task Close_UnknownSession_Returns404()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = fixture.Sessions.CreateClient();

        // Act
        using var response = await SessionApi.CloseAsync(client, Guid.CreateVersion7(), cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
