using System.Net.Http.Json;
using SmartParking.Api.Features.Facilities;
using SmartParking.Domain.Facilities;

namespace SmartParking.Api.Tests.Fixtures;

/// <summary>Facility requests for the tests: unique names (no clean-up between tests), the ETag handling and audit queries.</summary>
internal static class FacilityApi
{
    public const string Route = "/api/facilities";

    public static CreateFacilityRequest NewRequest(string? name = null, IReadOnlyList<OpeningHoursRequest>? hours = null) => new(
        Operator.ReferenceOperatorId,
        name ?? $"Facility {Guid.NewGuid():N}",
        "Rua da Sofia 1, Coimbra",
        40.2033,
        -8.4103,
        "Europe/Lisbon",
        hours ?? [new OpeningHoursRequest(1, new TimeOnly(8, 0), new TimeOnly(20, 0))]);

    public static UpdateFacilityRequest UpdateOf(CreateFacilityRequest created, string name, string status = "Active") => new(
        name, created.Address, created.Latitude, created.Longitude, created.TimeZone, status, created.OpeningHours);

    /// <summary>Creates a facility and returns it with its ETag.</summary>
    public static async Task<(FacilityResponse Facility, string ETag)> CreateAsync(
        HttpClient client, CancellationToken cancellationToken, CreateFacilityRequest? request = null)
    {
        using var response = await client.PostAsJsonAsync(Route, request ?? NewRequest(), cancellationToken);
        response.EnsureSuccessStatusCode();

        var facility = await response.Content.ReadFromJsonAsync<FacilityResponse>(cancellationToken);
        return (facility!, response.Headers.ETag!.Tag);
    }

    public static HttpRequestMessage WithIfMatch(HttpMethod method, Guid id, string? etag, object? body = null)
    {
        var request = new HttpRequestMessage(method, new Uri($"{Route}/{id}", UriKind.Relative))
        {
            Content = body is null ? null : JsonContent.Create(body),
        };

        if (etag is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", etag);
        }

        return request;
    }

    /// <summary>Outbox types whose payload names this facility, oldest first (also orphans without an audit entry).</summary>
    public static Task<List<string>> OutboxTypesAsync(string connectionString, Guid facilityId, CancellationToken cancellationToken) =>
        DbProbe.ColumnAsync(
            connectionString,
            "SELECT type FROM outbox_messages WHERE payload->>'id' = $1 ORDER BY occurred_at, id",
            cancellationToken,
            facilityId.ToString());

    /// <summary>Audit entries of one entity, oldest first: <c>operation|outboxType</c>.</summary>
    public static Task<List<string>> AuditTrailAsync(string connectionString, Guid entityId, CancellationToken cancellationToken) =>
        DbProbe.ColumnAsync(
            connectionString,
            """
            SELECT a.operation || '|' || coalesce(o.type, 'missing')
            FROM audit_entries a LEFT JOIN outbox_messages o ON o.id = a.outbox_message_id
            WHERE a.entity_id = $1
            ORDER BY a.occurred_at, a.id
            """,
            cancellationToken,
            entityId);
}
