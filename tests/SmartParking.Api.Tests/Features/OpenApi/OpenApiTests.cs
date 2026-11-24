using System.Net;
using SmartParking.Api.Tests.Fixtures;
using SmartParking.Infrastructure;
using SmartParking.Tests.Common;

namespace SmartParking.Api.Tests.Features.OpenApi;

public sealed class OpenApiTests
{
    private const string Document = "/openapi/v1.json";

    [Theory]
    [InlineData("Development", HttpStatusCode.OK)]
    [InlineData("Production", HttpStatusCode.NotFound)]
    public async Task GetOpenApiDocument_ByEnvironment_IsOnlyServedInDevelopment_AC13(string environment, HttpStatusCode expected)
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = ApiFixture.CreateFactory(
            DatabaseContainers.UnreachableSettings(PersistenceMode.Hybrid),
            environment);
        using var client = factory.CreateClient();

        // Act
        using var response = await client.GetAsync(new Uri(Document, UriKind.Relative), cancellationToken);

        // Assert
        Assert.Equal(expected, response.StatusCode);
    }
}
