using System.Net;
using System.Net.Http.Json;
using SmartParking.Api.Features.Sessions;
using SmartParking.Api.Tests.Fixtures;

namespace SmartParking.Api.Tests.Features.Sessions;

/// <summary>AC-19 (spec 002): reading a session.</summary>
[Collection(ApiCollectionDefinition.Name)]
public sealed class GetSessionTests(ApiFixture fixture)
{
    private static readonly DateTimeOffset Wednesday10 = new(2026, 12, 2, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Get_OpenThenClosed_AmountNullThenValue_AC19()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        fixture.Clock.SetUtcNow(Wednesday10);
        using var client = fixture.Sessions.CreateClient();
        var arranged = await SessionApi.ArrangeAsync(client, fixture.Clock, cancellationToken);
        var opened = await SessionApi.OpenOkAsync(client, arranged.SpotId, arranged.VehicleId, cancellationToken);
        var route = $"{SessionApi.Route}/{opened.Id}";

        // Act
        var whileOpen = await client.GetFromJsonAsync<SessionResponse>(route, cancellationToken);
        fixture.Clock.Advance(TimeSpan.FromMinutes(40));
        await SessionApi.CloseOkAsync(client, opened.Id, cancellationToken);
        var afterClose = await client.GetFromJsonAsync<SessionResponse>(route, cancellationToken);

        // Assert
        Assert.Equal("Open", whileOpen!.Status);
        Assert.Null(whileOpen.Amount);
        Assert.Equal("EUR", whileOpen.Currency);
        Assert.Equal("Closed", afterClose!.Status);
        Assert.Equal(0.90m, afterClose.Amount);
        Assert.Equal(Wednesday10.AddMinutes(40), afterClose.EndedAt);
    }

    [Fact]
    public async Task Get_UnknownId_Returns404_AC19()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = fixture.Sessions.CreateClient();

        // Act
        using var response = await client.GetAsync(new Uri($"{SessionApi.Route}/{Guid.CreateVersion7()}", UriKind.Relative), cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }
}
