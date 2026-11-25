using System.Net;
using System.Net.Http.Json;
using SmartParking.Api.Features.Users;
using SmartParking.Api.Tests.Fixtures;

namespace SmartParking.Api.Tests.Features.Users;

/// <summary>AC-2 (spec 002): registering a driver.</summary>
[Collection(ApiCollectionDefinition.Name)]
public sealed class RegisterUserTests(ApiFixture fixture)
{
    private string App => fixture.Databases.AppConnectionString;

    [Fact]
    public async Task Register_Valid_Returns201LowerCaseEmailOneAuditOneEvent_AC2()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = fixture.Sessions.CreateClient();
        var email = UserApi.NewEmail();

        // Act
        using var response = await client.PostAsJsonAsync(UserApi.Route, new RegisterUserRequest("Ana", email), cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var user = (await response.Content.ReadFromJsonAsync<UserResponse>(cancellationToken))!;
        Assert.Equal($"/api/users/{user.Id}", response.Headers.Location?.AbsolutePath);
        Assert.Equal(email.ToLowerInvariant(), user.Email);
        Assert.Equal(email.ToLowerInvariant(), await DbProbe.ScalarAsync<string>(App, "SELECT email FROM users WHERE id = $1", cancellationToken, user.Id));
        Assert.Equal(["User|Created|UserRegistered.v1"], await UserApi.AuditAsync(App, user.Id, cancellationToken));
        Assert.Equal(["UserRegistered.v1"], await UserApi.OutboxTypesAsync(App, user.Id, cancellationToken));
    }

    [Fact]
    public async Task Register_EmailDifferentCase_Returns409_AC2()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = fixture.Sessions.CreateClient();
        var email = UserApi.NewEmail();
        await UserApi.PostAsync<UserResponse>(client, UserApi.Route, new RegisterUserRequest("Ana", email), cancellationToken);

        // Act
        using var response = await client.PostAsJsonAsync(
            UserApi.Route, new RegisterUserRequest("Ana", email.ToUpperInvariant()), cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(1, await DbProbe.ScalarAsync<long>(
            App, "SELECT count(*) FROM users WHERE email = $1", cancellationToken, email.ToLowerInvariant()));
    }

    [Theory]
    [InlineData("", "ana@example.pt")]
    [InlineData("Ana", "not-an-email")]
    public async Task Register_InvalidInput_Returns400(string name, string email)
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = fixture.Sessions.CreateClient();

        // Act
        using var response = await client.PostAsJsonAsync(UserApi.Route, new RegisterUserRequest(name, email), cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Get_UnknownUser_Returns404()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = fixture.Sessions.CreateClient();

        // Act
        using var response = await client.GetAsync(new Uri($"{UserApi.Route}/{Guid.CreateVersion7()}", UriKind.Relative), cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
