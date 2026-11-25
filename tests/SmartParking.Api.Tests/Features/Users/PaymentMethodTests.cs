using System.Net;
using System.Net.Http.Json;
using SmartParking.Api.Features.Users;
using SmartParking.Api.Tests.Fixtures;

namespace SmartParking.Api.Tests.Features.Users;

/// <summary>AC-4 (spec 002): the provider token of a payment method never leaves the payment_methods table.</summary>
[Collection(ApiCollectionDefinition.Name)]
public sealed class PaymentMethodTests(ApiFixture fixture)
{
    private string App => fixture.Databases.AppConnectionString;

    [Fact]
    public async Task Add_ProviderToken_NotInListAuditOrOutbox_AC4()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = fixture.Sessions.CreateClient();
        var user = await UserApi.CreateAsync(client, cancellationToken);
        var token = UserApi.NewToken();

        // Act
        using var created = await client.PostAsJsonAsync(
            UserApi.PaymentMethods(user.Id), new AddPaymentMethodRequest("Card", token, "**** 4242", new DateOnly(2028, 12, 31)), cancellationToken);
        var createdBody = await created.Content.ReadAsStringAsync(cancellationToken);
        var listBody = await client.GetStringAsync(new Uri(UserApi.PaymentMethods(user.Id), UriKind.Relative), cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(token, await DbProbe.ScalarAsync<string>(
            App, "SELECT provider_token FROM payment_methods WHERE user_id = $1", cancellationToken, user.Id));
        Assert.DoesNotContain(token, createdBody, StringComparison.Ordinal);
        Assert.Contains("**** 4242", listBody, StringComparison.Ordinal);
        Assert.DoesNotContain(token, listBody, StringComparison.Ordinal);
        Assert.Equal(1, await DbProbe.ScalarAsync<long>(
            App, "SELECT count(*) FROM audit_entries WHERE entity_type = 'PaymentMethod' AND after->>'userId' = $1", cancellationToken, user.Id.ToString()));
        Assert.Equal(0, await DbProbe.ScalarAsync<long>(
            App,
            "SELECT count(*) FROM audit_entries WHERE strpos(coalesce(before::text, '') || coalesce(after::text, ''), $1) > 0",
            cancellationToken,
            token));
        Assert.Equal(0, await DbProbe.ScalarAsync<long>(
            App, "SELECT count(*) FROM outbox_messages WHERE strpos(payload::text, $1) > 0", cancellationToken, token));
    }

    [Fact]
    public async Task Delete_MethodWithoutPayments_Returns204WithAuditAndEvent()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = fixture.Sessions.CreateClient();
        var user = await UserApi.CreateAsync(client, cancellationToken);
        var method = await UserApi.AddPaymentMethodAsync(client, user.Id, cancellationToken);
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));

        // Act
        using var response = await client.DeleteAsync(
            new Uri($"{UserApi.PaymentMethods(user.Id)}/{method.Id}", UriKind.Relative), cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(
            ["PaymentMethod|Created|PaymentMethodAdded.v1", "PaymentMethod|Deleted|PaymentMethodRemoved.v1"],
            await UserApi.AuditAsync(App, method.Id, cancellationToken));
    }

    [Fact]
    public async Task Add_UnknownUser_Returns404()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = fixture.Sessions.CreateClient();

        // Act
        using var response = await client.PostAsJsonAsync(
            UserApi.PaymentMethods(Guid.CreateVersion7()), new AddPaymentMethodRequest("Card", "tok", "**** 1", null), cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Add_UnknownType_Returns400()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = fixture.Sessions.CreateClient();
        var user = await UserApi.CreateAsync(client, cancellationToken);

        // Act
        using var response = await client.PostAsJsonAsync(
            UserApi.PaymentMethods(user.Id), new AddPaymentMethodRequest("card", "tok", "**** 1", null), cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
