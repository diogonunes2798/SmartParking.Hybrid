using System.Net;
using System.Text.Json;
using SmartParking.Api.Tests.Fixtures;
using SmartParking.Application.Abstractions;
using SmartParking.Infrastructure;
using SmartParking.Tests.Common;

namespace SmartParking.Api.Tests.Features.Errors;

public sealed class ErrorHandlingTests
{
    private const string ProblemJson = "application/problem+json";

    [Fact]
    public async Task UnhandledException_OutsideDevelopment_Returns500ProblemWithTraceIdAndNoDetails_AC10()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = ApiFixture.CreateFactory(
            DatabaseContainers.UnreachableSettings(PersistenceMode.Hybrid),
            environment: "Production");
        using var client = factory.CreateClient();

        // Act
        using var response = await client.GetAsync(new Uri(TestEndpoints.Throw, UriKind.Relative), cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(ProblemJson, response.Content.Headers.ContentType?.MediaType);
        using var problem = JsonDocument.Parse(body);
        Assert.Equal(500, problem.RootElement.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(problem.RootElement.GetProperty("traceId").GetString()));
        Assert.DoesNotContain(TestEndpoints.SecretMessage, body, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(InvalidOperationException), body, StringComparison.Ordinal);
        Assert.DoesNotContain(" at ", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ErrorType.NotFound, HttpStatusCode.NotFound)]
    [InlineData(ErrorType.Validation, HttpStatusCode.BadRequest)]
    [InlineData(ErrorType.Conflict, HttpStatusCode.Conflict)]
    [InlineData(ErrorType.Unauthorized, HttpStatusCode.Unauthorized)]
    public async Task ErrorToProblem_EachErrorType_MapsToStatusCodeAsProblemDetails_AC11(ErrorType type, HttpStatusCode expected)
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = ApiFixture.CreateFactory(DatabaseContainers.UnreachableSettings(PersistenceMode.Hybrid));
        using var client = factory.CreateClient();

        // Act
        using var response = await client.GetAsync(new Uri($"{TestEndpoints.Errors}/{type}", UriKind.Relative), cancellationToken);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));

        // Assert
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(ProblemJson, response.Content.Headers.ContentType?.MediaType);
        Assert.Equal((int)expected, problem.RootElement.GetProperty("status").GetInt32());
        Assert.True(problem.RootElement.TryGetProperty("traceId", out _));
        Assert.Equal(type == ErrorType.Validation, problem.RootElement.TryGetProperty("errors", out _));
    }

    [Theory]
    [InlineData(ErrorType.PreconditionFailed, HttpStatusCode.PreconditionFailed)]
    [InlineData(ErrorType.PreconditionRequired, HttpStatusCode.PreconditionRequired)]
    public async Task ErrorToProblem_PreconditionErrors_Map412And428AsProblemDetails_AC20(ErrorType type, HttpStatusCode expected)
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = ApiFixture.CreateFactory(DatabaseContainers.UnreachableSettings(PersistenceMode.Hybrid));
        using var client = factory.CreateClient();

        // Act
        using var response = await client.GetAsync(new Uri($"{TestEndpoints.Errors}/{type}", UriKind.Relative), cancellationToken);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));

        // Assert
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(ProblemJson, response.Content.Headers.ContentType?.MediaType);
        Assert.Equal((int)expected, problem.RootElement.GetProperty("status").GetInt32());
        Assert.Equal($"Test.{type}", problem.RootElement.GetProperty("title").GetString());
        Assert.True(problem.RootElement.TryGetProperty("traceId", out _));
        Assert.False(problem.RootElement.TryGetProperty("errors", out _));
    }

    [Fact]
    public async Task ErrorToProblem_PaymentDeclined_Maps402WithPaymentId()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = ApiFixture.CreateFactory(DatabaseContainers.UnreachableSettings(PersistenceMode.Hybrid));
        using var client = factory.CreateClient();

        // Act
        using var response = await client.GetAsync(
            new Uri($"{TestEndpoints.Errors}/{ErrorType.PaymentDeclined}", UriKind.Relative), cancellationToken);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));

        // Assert
        Assert.Equal(HttpStatusCode.PaymentRequired, response.StatusCode);
        Assert.Equal(ProblemJson, response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(402, problem.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(TestEndpoints.DeclinedPaymentId, problem.RootElement.GetProperty("paymentId").GetGuid());
        Assert.True(problem.RootElement.TryGetProperty("traceId", out _));
    }
}
