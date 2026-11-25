using System.Net.Http.Json;
using SmartParking.Api.Features.Users;

namespace SmartParking.Api.Tests.Fixtures;

/// <summary>Requests for users, vehicles and payment methods (spec 002): unique data per test, no clean-up between tests.</summary>
internal static class UserApi
{
    public const string Route = "/api/users";

    public static string Vehicles(Guid userId) => $"{Route}/{userId}/vehicles";

    public static string PaymentMethods(Guid userId) => $"{Route}/{userId}/payment-methods";

    /// <summary>A unique email in mixed case; the API stores it in lower case.</summary>
    public static string NewEmail() => $"Driver.{Guid.NewGuid():N}@Example.Test";

    /// <summary>A unique plate written with spaces and a hyphen, so the API has to normalise it.</summary>
    public static string NewPlate()
    {
        var letters = Guid.NewGuid().ToString("N")[..10];
        return $"{letters[..2]}-{letters[2..6]} {letters[6..]}";
    }

    public static string NewToken() => $"tok_{Guid.NewGuid():N}";

    public static Task<UserResponse> CreateAsync(HttpClient client, CancellationToken cancellationToken) =>
        PostAsync<UserResponse>(client, Route, new RegisterUserRequest("Driver", NewEmail()), cancellationToken);

    public static Task<VehicleResponse> RegisterVehicleAsync(
        HttpClient client, Guid userId, CancellationToken cancellationToken, string? plate = null) =>
        PostAsync<VehicleResponse>(client, Vehicles(userId), new RegisterVehicleRequest(plate ?? NewPlate()), cancellationToken);

    public static Task<PaymentMethodResponse> AddPaymentMethodAsync(
        HttpClient client, Guid userId, CancellationToken cancellationToken, string? token = null) =>
        PostAsync<PaymentMethodResponse>(
            client, PaymentMethods(userId), new AddPaymentMethodRequest("Card", token ?? NewToken(), "**** 4242", null), cancellationToken);

    public static async Task<T> PostAsync<T>(HttpClient client, string route, object body, CancellationToken cancellationToken)
    {
        using var response = await client.PostAsJsonAsync(route, body, cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>(cancellationToken))!;
    }

    /// <summary>Audit entries of one entity: <c>entityType|operation|outboxType</c>, oldest first.</summary>
    public static Task<List<string>> AuditAsync(string connectionString, Guid entityId, CancellationToken cancellationToken) =>
        DbProbe.ColumnAsync(
            connectionString,
            """
            SELECT a.entity_type || '|' || a.operation || '|' || coalesce(o.type, 'missing')
            FROM audit_entries a LEFT JOIN outbox_messages o ON o.id = a.outbox_message_id
            WHERE a.entity_id = $1
            ORDER BY a.occurred_at, a.id
            """,
            cancellationToken,
            entityId);

    /// <summary>Outbox types whose payload names this entity as <c>id</c>, oldest first.</summary>
    public static Task<List<string>> OutboxTypesAsync(string connectionString, Guid entityId, CancellationToken cancellationToken) =>
        DbProbe.ColumnAsync(
            connectionString,
            "SELECT type FROM outbox_messages WHERE payload->>'id' = $1 ORDER BY occurred_at, id",
            cancellationToken,
            entityId.ToString());
}
