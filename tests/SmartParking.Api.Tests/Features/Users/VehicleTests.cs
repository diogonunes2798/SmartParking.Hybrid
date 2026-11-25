using System.Net;
using System.Net.Http.Json;
using SmartParking.Api.Common;
using SmartParking.Api.Features.Users;
using SmartParking.Api.Tests.Fixtures;

namespace SmartParking.Api.Tests.Features.Users;

/// <summary>AC-3 (spec 002): vehicles of a user.</summary>
[Collection(ApiCollectionDefinition.Name)]
public sealed class VehicleTests(ApiFixture fixture)
{
    private string App => fixture.Databases.AppConnectionString;

    [Fact]
    public async Task Register_PlateWithSpacesAndHyphen_NormalisesAndReturns201WithAuditAndEvent_AC3()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = fixture.Sessions.CreateClient();
        var user = await UserApi.CreateAsync(client, cancellationToken);
        var suffix = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

        // Act
        using var response = await client.PostAsJsonAsync(
            UserApi.Vehicles(user.Id), new RegisterVehicleRequest($"aa-12 bb{suffix.ToLowerInvariant()}"), cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var vehicle = (await response.Content.ReadFromJsonAsync<VehicleResponse>(cancellationToken))!;
        Assert.Equal($"AA12BB{suffix}", vehicle.LicensePlate);
        Assert.Equal(["Vehicle|Created|VehicleRegistered.v1"], await UserApi.AuditAsync(App, vehicle.Id, cancellationToken));
        Assert.Equal(["VehicleRegistered.v1"], await UserApi.OutboxTypesAsync(App, vehicle.Id, cancellationToken));
        Assert.Equal(["User|Created|UserRegistered.v1"], await UserApi.AuditAsync(App, user.Id, cancellationToken));
    }

    [Fact]
    public async Task Register_DuplicatePlate_Returns409_AC3()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = fixture.Sessions.CreateClient();
        var user = await UserApi.CreateAsync(client, cancellationToken);
        var other = await UserApi.CreateAsync(client, cancellationToken);
        var plate = UserApi.NewPlate();
        await UserApi.RegisterVehicleAsync(client, user.Id, cancellationToken, plate);

        // Act
        using var response = await client.PostAsJsonAsync(
            UserApi.Vehicles(other.Id), new RegisterVehicleRequest(plate.Replace("-", string.Empty, StringComparison.Ordinal)), cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Delete_VehicleWithSession_Returns409AndWritesNothing_AC3()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = fixture.Sessions.CreateClient();
        var arranged = await SessionApi.ArrangeAsync(client, fixture.Clock, cancellationToken);
        await SessionApi.OpenOkAsync(client, arranged.SpotId, arranged.VehicleId, cancellationToken);
        var auditBefore = await UserApi.AuditAsync(App, arranged.VehicleId, cancellationToken);

        // Act
        using var response = await client.DeleteAsync(
            new Uri($"{UserApi.Vehicles(arranged.UserId)}/{arranged.VehicleId}", UriKind.Relative), cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(1, await DbProbe.ScalarAsync<long>(App, "SELECT count(*) FROM vehicles WHERE id = $1", cancellationToken, arranged.VehicleId));
        Assert.Equal(auditBefore, await UserApi.AuditAsync(App, arranged.VehicleId, cancellationToken));
        Assert.Equal(["VehicleRegistered.v1"], await UserApi.OutboxTypesAsync(App, arranged.VehicleId, cancellationToken));
    }

    [Fact]
    public async Task Delete_FreeVehicle_Returns204WithAuditAndEvent()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = fixture.Sessions.CreateClient();
        var user = await UserApi.CreateAsync(client, cancellationToken);
        var vehicle = await UserApi.RegisterVehicleAsync(client, user.Id, cancellationToken);
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));

        // Act
        using var response = await client.DeleteAsync(new Uri($"{UserApi.Vehicles(user.Id)}/{vehicle.Id}", UriKind.Relative), cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(
            ["Vehicle|Created|VehicleRegistered.v1", "Vehicle|Deleted|VehicleRemoved.v1"],
            await UserApi.AuditAsync(App, vehicle.Id, cancellationToken));
    }

    [Fact]
    public async Task Delete_VehicleOfOtherUser_Returns404()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = fixture.Sessions.CreateClient();
        var owner = await UserApi.CreateAsync(client, cancellationToken);
        var other = await UserApi.CreateAsync(client, cancellationToken);
        var vehicle = await UserApi.RegisterVehicleAsync(client, owner.Id, cancellationToken);

        // Act
        using var response = await client.DeleteAsync(new Uri($"{UserApi.Vehicles(other.Id)}/{vehicle.Id}", UriKind.Relative), cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task List_TwoVehicles_ReturnsPage()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = fixture.Sessions.CreateClient();
        var user = await UserApi.CreateAsync(client, cancellationToken);
        await UserApi.RegisterVehicleAsync(client, user.Id, cancellationToken);
        await UserApi.RegisterVehicleAsync(client, user.Id, cancellationToken);

        // Act
        var page = await client.GetFromJsonAsync<PagedResponse<VehicleResponse>>(UserApi.Vehicles(user.Id), cancellationToken);

        // Assert
        Assert.Equal(2, page!.TotalCount);
        Assert.All(page.Items, vehicle => Assert.Equal(user.Id, vehicle.UserId));
    }

    [Theory]
    [InlineData("AA/12")]
    [InlineData("12345678901234567")]
    [InlineData(" - ")]
    public async Task Register_InvalidPlate_Returns400(string plate)
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = fixture.Sessions.CreateClient();
        var user = await UserApi.CreateAsync(client, cancellationToken);

        // Act
        using var response = await client.PostAsJsonAsync(UserApi.Vehicles(user.Id), new RegisterVehicleRequest(plate), cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
