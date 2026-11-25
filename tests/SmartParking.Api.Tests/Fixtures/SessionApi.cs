using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using SmartParking.Api.Features.Facilities;
using SmartParking.Api.Features.Sessions;
using SmartParking.Api.Features.Tariffs;
using SmartParking.Application.Abstractions.Payments;
using SmartParking.Infrastructure.Payments;
using SmartParking.Tests.Common;

namespace SmartParking.Api.Tests.Fixtures;

/// <summary>
/// Arranges an openable spot through the API (spec 002): a facility in Europe/Lisbon open 08:00–20:00 every day, a
/// zone, a spot, the reference tariff of the spec in force from the clock's now (1.20 €/h, 15 min, 15 free minutes,
/// daily maximum 10.00 €), a user with a vehicle and a payment method. Also the session requests and probes.
/// </summary>
internal static class SessionApi
{
    public const string Route = "/api/sessions";

    public static readonly OpeningHoursRequest[] EveryDay8To20 =
        [.. Enumerable.Range(0, 7).Select(day => new OpeningHoursRequest(day, new TimeOnly(8, 0), new TimeOnly(20, 0)))];

    public static string Close(Guid sessionId) => $"{Route}/{sessionId}/close";

    public static string Pay(Guid sessionId) => $"{Route}/{sessionId}/pay";

    public static CreateTariffRequest ReferenceTariff(DateTimeOffset validFrom, decimal pricePerHour = 1.20m) =>
        new(validFrom, pricePerHour, 15, 10.00m, 15, null);

    public static async Task<Arranged> ArrangeAsync(
        HttpClient client, FakeTimeProvider clock, CancellationToken cancellationToken, IReadOnlyList<OpeningHoursRequest>? hours = null)
    {
        var (facility, facilityETag) = await FacilityApi.CreateAsync(
            client, cancellationToken, FacilityApi.NewRequest(hours: hours ?? EveryDay8To20));
        var (zone, _) = await ConfigurationApi.CreateZoneAsync(client, facility.Id, cancellationToken);
        var (spot, spotETag) = await ConfigurationApi.CreateSpotAsync(client, facility.Id, zone.Id, cancellationToken);
        var (tariff, _) = await ConfigurationApi.PostAsync<TariffResponse>(
            client, ConfigurationApi.Tariffs(facility.Id), ReferenceTariff(clock.GetUtcNow()), cancellationToken);
        var user = await UserApi.CreateAsync(client, cancellationToken);
        var vehicle = await UserApi.RegisterVehicleAsync(client, user.Id, cancellationToken);
        var method = await UserApi.AddPaymentMethodAsync(client, user.Id, cancellationToken);

        return new Arranged(facility, facilityETag, zone.Id, spot.Id, spotETag, tariff.Id, user.Id, vehicle.Id, method.Id);
    }

    /// <summary>A second vehicle and payment method of a new user.</summary>
    public static async Task<(Guid UserId, Guid VehicleId, Guid PaymentMethodId)> OtherDriverAsync(
        HttpClient client, CancellationToken cancellationToken, string? token = null)
    {
        var user = await UserApi.CreateAsync(client, cancellationToken);
        var vehicle = await UserApi.RegisterVehicleAsync(client, user.Id, cancellationToken);
        var method = await UserApi.AddPaymentMethodAsync(client, user.Id, cancellationToken, token);
        return (user.Id, vehicle.Id, method.Id);
    }

    public static Task<HttpResponseMessage> OpenAsync(
        HttpClient client, Guid spotId, Guid vehicleId, CancellationToken cancellationToken, Guid? reservationId = null) =>
        client.PostAsJsonAsync(Route, new OpenSessionRequest(spotId, vehicleId, reservationId), cancellationToken);

    public static async Task<SessionResponse> OpenOkAsync(HttpClient client, Guid spotId, Guid vehicleId, CancellationToken cancellationToken)
    {
        using var response = await OpenAsync(client, spotId, vehicleId, cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<SessionResponse>(cancellationToken))!;
    }

    public static Task<HttpResponseMessage> CloseAsync(HttpClient client, Guid sessionId, CancellationToken cancellationToken) =>
        client.PostAsync(new Uri(Close(sessionId), UriKind.Relative), null, cancellationToken);

    public static async Task<SessionResponse> CloseOkAsync(HttpClient client, Guid sessionId, CancellationToken cancellationToken)
    {
        using var response = await CloseAsync(client, sessionId, cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<SessionResponse>(cancellationToken))!;
    }

    public static Task<HttpResponseMessage> PayAsync(HttpClient client, Guid sessionId, Guid paymentMethodId, CancellationToken cancellationToken) =>
        client.PostAsJsonAsync(Pay(sessionId), new PaySessionRequest(paymentMethodId), cancellationToken);

    /// <summary>
    /// Opens a session now and closes it after <paramref name="duration"/> on the clock. The clock then moves one more
    /// minute, so the next transition is not written at the same instant: audit and outbox rows are listed by
    /// <c>occurred_at</c>, and UUID v7 ids of the same millisecond have no reliable order.
    /// </summary>
    public static async Task<SessionResponse> ClosedSessionAsync(
        HttpClient client, FakeTimeProvider clock, Arranged arranged, TimeSpan duration, CancellationToken cancellationToken)
    {
        var opened = await OpenOkAsync(client, arranged.SpotId, arranged.VehicleId, cancellationToken);
        clock.Advance(duration);
        var closed = await CloseOkAsync(client, opened.Id, cancellationToken);
        clock.Advance(TimeSpan.FromMinutes(1));
        return closed;
    }

    public static int ChargeCount(IServiceProvider services, Guid sessionId, Guid paymentMethodId) =>
        ((FakePaymentGateway)services.GetRequiredService<IPaymentGateway>()).ChargeCount(new PaymentIdempotencyKey(sessionId, paymentMethodId));

    /// <summary>Outbox types of one session (as <c>id</c> or <c>sessionId</c> in the payload), oldest first.</summary>
    public static Task<List<string>> SessionOutboxTypesAsync(string connectionString, Guid sessionId, CancellationToken cancellationToken) =>
        DbProbe.ColumnAsync(
            connectionString,
            "SELECT type FROM outbox_messages WHERE payload->>'id' = $1 OR payload->>'sessionId' = $1 ORDER BY occurred_at, id",
            cancellationToken,
            sessionId.ToString());

    public static Task<long> CountAsync(string connectionString, string sql, CancellationToken cancellationToken, params object[] parameters) =>
        DbProbe.ScalarAsync<long>(connectionString, sql, cancellationToken, parameters);

    internal sealed record Arranged(
        FacilityResponse Facility,
        string FacilityETag,
        Guid ZoneId,
        Guid SpotId,
        string SpotETag,
        Guid TariffId,
        Guid UserId,
        Guid VehicleId,
        Guid PaymentMethodId);
}
