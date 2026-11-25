using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SmartParking.Api.Common;
using SmartParking.Api.Features.Facilities;
using SmartParking.Api.Tests.Fixtures;

namespace SmartParking.Api.Tests.Features.Facilities;

/// <summary>
/// Reading and paging (spec 001, AC-18). F2a pages facilities; F2b adds the spot listing.
/// Exact counts run against an isolated database, because the shared one holds other tests' facilities.
/// </summary>
[Collection(ApiCollectionDefinition.Name)]
public sealed class ListFacilitiesTests(ApiFixture fixture)
{
    [Fact]
    public async Task List_Page2Size10Of25_Returns10AndTotal25_AC18()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        var database = await fixture.Databases.CreateIsolatedDatabaseAsync(cancellationToken);
        await using var factory = fixture.CreateFactory(database);
        using var client = factory.CreateClient();
        for (var i = 0; i < 25; i++)
        {
            await FacilityApi.CreateAsync(client, cancellationToken, FacilityApi.NewRequest($"Paged {i:D2}"));
        }

        // Act
        var page = await client.GetFromJsonAsync<PagedResponse<FacilityResponse>>(
            $"{FacilityApi.Route}?page=2&pageSize=10", cancellationToken);

        // Assert
        Assert.Equal(10, page!.Items.Count);
        Assert.Equal(2, page.Page);
        Assert.Equal(10, page.PageSize);
        Assert.Equal(25, page.TotalCount);
        Assert.Equal([.. Enumerable.Range(10, 10).Select(i => $"Paged {i:D2}")], page.Items.Select(item => item.Name));
    }

    [Fact]
    public async Task List_PageSize101_Returns400_AC18()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = fixture.Hybrid.CreateClient();

        // Act
        using var response = await client.GetAsync(new Uri($"{FacilityApi.Route}?pageSize=101", UriKind.Relative), cancellationToken);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty("pageSize", out _));
    }

    [Fact]
    public async Task Get_UnknownFacility_Returns404_AC18()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = fixture.Hybrid.CreateClient();

        // Act
        using var response = await client.GetAsync(new Uri($"{FacilityApi.Route}/{Guid.CreateVersion7()}", UriKind.Relative), cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }
}
