using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SmartParking.Api.Tests.Fixtures;
using SmartParking.Infrastructure;
using SmartParking.Tests.Common;

namespace SmartParking.Api.Tests.Features.Validation;

public sealed class ValidationFilterTests
{
    [Fact]
    public async Task ValidationFilter_InvalidRequest_Returns400ValidationProblemAndSkipsHandler_AC12()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = ApiFixture.CreateFactory(DatabaseContainers.UnreachableSettings(PersistenceMode.Hybrid));
        using var client = factory.CreateClient();
        var probe = factory.Services.GetRequiredService<ValidationProbe>();

        // Act
        using var response = await client.PostAsJsonAsync(TestEndpoints.Validated, new TestRequest(Name: ""), cancellationToken);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty("Name", out _));
        Assert.Equal(0, probe.Calls);
    }

    [Fact]
    public async Task ValidationFilter_ValidRequest_CallsHandler_AC12()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = ApiFixture.CreateFactory(DatabaseContainers.UnreachableSettings(PersistenceMode.Hybrid));
        using var client = factory.CreateClient();
        var probe = factory.Services.GetRequiredService<ValidationProbe>();

        // Act
        using var response = await client.PostAsJsonAsync(TestEndpoints.Validated, new TestRequest(Name: "spot"), cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, probe.Calls);
    }
}
