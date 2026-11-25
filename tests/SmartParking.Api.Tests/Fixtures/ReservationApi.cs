using System.Net.Http.Json;
using SmartParking.Api.Features.Reservations;

namespace SmartParking.Api.Tests.Fixtures;

/// <summary>
/// Reservation requests and probes (spec 002, F3b). The reference reservation is Wednesday 2026-12-02 11:00–12:00,
/// in December and therefore UTC+0 in Lisbon, inside the 08:00–20:00 hours of <see cref="SessionApi.ArrangeAsync"/>.
/// </summary>
internal static class ReservationApi
{
    public const string Route = "/api/reservations";

    public static readonly DateTimeOffset ReferenceStart = new(2026, 12, 2, 11, 0, 0, TimeSpan.Zero);

    public static readonly DateTimeOffset ReferenceEnd = ReferenceStart.AddHours(1);

    public static string Cancel(Guid reservationId) => $"{Route}/{reservationId}/cancel";

    public static CreateReservationRequest Request(
        SessionApi.Arranged arranged, DateTimeOffset? startsAt = null, DateTimeOffset? endsAt = null, Guid? spotId = null) =>
        new(spotId ?? arranged.SpotId, arranged.UserId, arranged.VehicleId, startsAt ?? ReferenceStart, endsAt ?? ReferenceEnd);

    public static Task<HttpResponseMessage> CreateAsync(HttpClient client, CreateReservationRequest request, CancellationToken cancellationToken) =>
        client.PostAsJsonAsync(Route, request, cancellationToken);

    public static async Task<ReservationResponse> CreateOkAsync(
        HttpClient client, CreateReservationRequest request, CancellationToken cancellationToken)
    {
        using var response = await CreateAsync(client, request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ReservationResponse>(cancellationToken))!;
    }

    public static Task<HttpResponseMessage> CancelAsync(HttpClient client, Guid reservationId, CancellationToken cancellationToken) =>
        client.PostAsync(new Uri(Cancel(reservationId), UriKind.Relative), null, cancellationToken);

    public static Task<HttpResponseMessage> GetAsync(HttpClient client, Guid reservationId, CancellationToken cancellationToken) =>
        client.GetAsync(new Uri($"{Route}/{reservationId}", UriKind.Relative), cancellationToken);

    public static Task<string> StatusAsync(string connectionString, Guid reservationId, CancellationToken cancellationToken) =>
        DbProbe.ScalarAsync<string>(connectionString, "SELECT status FROM reservations WHERE id = $1", cancellationToken, reservationId);

    /// <summary>Reservations of a vehicle, whatever their status.</summary>
    public static Task<long> CountAsync(string connectionString, Guid vehicleId, CancellationToken cancellationToken) =>
        DbProbe.ScalarAsync<long>(connectionString, "SELECT count(*) FROM reservations WHERE vehicle_id = $1", cancellationToken, vehicleId);

    /// <summary>
    /// Outbox types that name this reservation: as <c>id</c> (its own events) or as <c>reservationId</c>
    /// (<c>SessionOpened.v1</c> of the session that fulfilled it), oldest first.
    /// </summary>
    public static Task<List<string>> OutboxTypesAsync(string connectionString, Guid reservationId, CancellationToken cancellationToken) =>
        DbProbe.ColumnAsync(
            connectionString,
            "SELECT type FROM outbox_messages WHERE payload->>'id' = $1 OR payload->>'reservationId' = $1 ORDER BY occurred_at, id",
            cancellationToken,
            reservationId.ToString());
}
