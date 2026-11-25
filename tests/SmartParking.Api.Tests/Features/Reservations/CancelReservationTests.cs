using System.Net;
using System.Net.Http.Json;
using SmartParking.Api.Features.Reservations;
using SmartParking.Api.Tests.Fixtures;

namespace SmartParking.Api.Tests.Features.Reservations;

/// <summary>AC-24 (spec 002): cancelling an active reservation; cancelling again is not idempotent.</summary>
[Collection(ApiCollectionDefinition.Name)]
public sealed class CancelReservationTests(ApiFixture fixture)
{
    private static readonly DateTimeOffset Wednesday10 = new(2026, 12, 2, 10, 0, 0, TimeSpan.Zero);

    private string App => fixture.Databases.AppConnectionString;

    [Theory]
    [InlineData(0)] // before startsAt
    [InlineData(70)] // after startsAt (11:10), still Active
    public async Task Cancel_Active_Returns200CancelledOneEvent_AC24(int minutesAfterCreate)
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        fixture.Clock.SetUtcNow(Wednesday10);
        using var client = fixture.Sessions.CreateClient();
        var arranged = await SessionApi.ArrangeAsync(client, fixture.Clock, cancellationToken);
        var created = await ReservationApi.CreateOkAsync(client, ReservationApi.Request(arranged), cancellationToken);
        fixture.Clock.Advance(TimeSpan.FromMinutes(minutesAfterCreate + 1));

        // Act
        using var response = await ReservationApi.CancelAsync(client, created.Id, cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var reservation = (await response.Content.ReadFromJsonAsync<ReservationResponse>(cancellationToken))!;
        Assert.Equal("Cancelled", reservation.Status);
        Assert.Equal("Cancelled", await ReservationApi.StatusAsync(App, created.Id, cancellationToken));
        Assert.Equal(
            ["ReservationCreated.v1", "ReservationCancelled.v1"],
            await ReservationApi.OutboxTypesAsync(App, created.Id, cancellationToken));
        Assert.Equal(
            ["Reservation|Created|ReservationCreated.v1", "Reservation|Updated|ReservationCancelled.v1"],
            await UserApi.AuditAsync(App, created.Id, cancellationToken));
    }

    [Fact]
    public async Task Cancel_Again_Returns409_AC24()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        fixture.Clock.SetUtcNow(Wednesday10);
        using var client = fixture.Sessions.CreateClient();
        var arranged = await SessionApi.ArrangeAsync(client, fixture.Clock, cancellationToken);
        var created = await ReservationApi.CreateOkAsync(client, ReservationApi.Request(arranged), cancellationToken);
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        using (var first = await ReservationApi.CancelAsync(client, created.Id, cancellationToken))
        {
            first.EnsureSuccessStatusCode();
        }

        fixture.Clock.Advance(TimeSpan.FromMinutes(1));

        // Act
        using var response = await ReservationApi.CancelAsync(client, created.Id, cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("Reservation.NotActive", await response.Content.ReadAsStringAsync(cancellationToken), StringComparison.Ordinal);
        Assert.Equal(
            ["ReservationCreated.v1", "ReservationCancelled.v1"],
            await ReservationApi.OutboxTypesAsync(App, created.Id, cancellationToken));
    }

    [Fact]
    public async Task Cancel_UnknownId_Returns404()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = fixture.Sessions.CreateClient();

        // Act
        using var response = await ReservationApi.CancelAsync(client, Guid.CreateVersion7(), cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
