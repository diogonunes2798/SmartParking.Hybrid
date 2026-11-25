using System.Net;
using System.Net.Http.Json;
using SmartParking.Api.Features.Reservations;
using SmartParking.Api.Tests.Fixtures;

namespace SmartParking.Api.Tests.Features.Reservations;

/// <summary>AC-30 (spec 002): reading a reservation with its current status.</summary>
[Collection(ApiCollectionDefinition.Name)]
public sealed class GetReservationTests(ApiFixture fixture)
{
    private static readonly DateTimeOffset Wednesday10 = new(2026, 12, 2, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Get_AfterCreateAndCancel_ReturnsCurrentStatus_AC30()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        fixture.Clock.SetUtcNow(Wednesday10);
        using var client = fixture.Sessions.CreateClient();
        var arranged = await SessionApi.ArrangeAsync(client, fixture.Clock, cancellationToken);
        var created = await ReservationApi.CreateOkAsync(client, ReservationApi.Request(arranged), cancellationToken);
        using var active = await ReservationApi.GetAsync(client, created.Id, cancellationToken);
        var beforeCancel = await active.Content.ReadFromJsonAsync<ReservationResponse>(cancellationToken);
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        using (var cancelled = await ReservationApi.CancelAsync(client, created.Id, cancellationToken))
        {
            cancelled.EnsureSuccessStatusCode();
        }

        // Act
        using var response = await ReservationApi.GetAsync(client, created.Id, cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.OK, active.StatusCode);
        Assert.Equal(created, beforeCancel);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var reservation = (await response.Content.ReadFromJsonAsync<ReservationResponse>(cancellationToken))!;
        Assert.Equal(created with { Status = "Cancelled" }, reservation);
    }

    [Fact]
    public async Task Get_UnknownId_Returns404_AC30()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = fixture.Sessions.CreateClient();

        // Act
        using var response = await ReservationApi.GetAsync(client, Guid.CreateVersion7(), cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }
}
