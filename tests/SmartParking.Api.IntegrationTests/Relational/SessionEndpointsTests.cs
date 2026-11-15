using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartParking.Application.Abstractions;
using SmartParking.Application.Common;
using SmartParking.Application.Configuration;
using SmartParking.Application.Sessions;
using SmartParking.Domain.Sessions;
using SmartParking.Domain.Tariffs;
using SmartParking.Infrastructure.Relational.Outbox;

namespace SmartParking.Api.IntegrationTests.Relational;

/// <summary>
/// UC-02 through HTTP: open, close and pay a session; each command writes its outbox event in the same
/// transaction (ADR-0004); conflicts are 409. Every test uses its own zone and tariff (no shared rows).
/// </summary>
[Collection(RelationalTestGroup.Name)]
public sealed class SessionEndpointsTests(RelationalFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private HttpClient Client => fixture.Client;

    /// <summary>The manual Swagger scenario of Phase 2b: three events, none processed yet.</summary>
    [Fact]
    public async Task Open_close_and_pay_write_three_unprocessed_outbox_events_in_order()
    {
        var setup = await SetUpAsync(new TariffRulesDto(1.20m, 0));

        var started = await StartAsync(setup);
        Assert.Equal(ParkingSessionStatus.Open, started.Status);

        var closeResponse = await Client.PostAsJsonAsync($"/api/sessions/{started.Id}/close", new CloseSession(), RelationalFixture.Json, Ct);
        Assert.Equal(HttpStatusCode.OK, closeResponse.StatusCode);
        var closed = (await closeResponse.Content.ReadFromJsonAsync<SessionDto>(RelationalFixture.Json, Ct))!;
        Assert.Equal(ParkingSessionStatus.Closed, closed.Status);
        Assert.Equal(setup.TariffId, closed.TariffId);
        Assert.Equal(
            new ProvisionalTariffPricing().Price(new TariffRules(1.20m, 0, null), closed.StartedAt, closed.EndedAt!.Value),
            closed.Cost);

        var payResponse = await Client.PostAsJsonAsync(
            $"/api/sessions/{started.Id}/pay", new PaySession(PaymentMethod.MbWay), RelationalFixture.Json, Ct);
        Assert.Equal(HttpStatusCode.Created, payResponse.StatusCode);
        var payment = (await payResponse.Content.ReadFromJsonAsync<PaymentDto>(RelationalFixture.Json, Ct))!;
        Assert.Equal(closed.Cost, payment.Amount);
        Assert.Equal(PaymentStatus.Completed, payment.Status);

        var session = await Client.GetFromJsonAsync<SessionDto>($"/api/sessions/{started.Id}", RelationalFixture.Json, Ct);
        Assert.Equal(ParkingSessionStatus.Paid, session!.Status);

        var events = await OutboxAsync(started.Id);
        Assert.Equal(["SessionStarted", "SessionClosed", "PaymentRegistered"], events.Select(e => e.Type));
        Assert.All(events, e => Assert.Null(e.ProcessedAt));

        // Data minimisation (RNF-06): identifiers and amounts only, never the user, the vehicle or their data.
        Assert.All(events, e =>
        {
            Assert.DoesNotContain(setup.UserId.ToString(), e.Payload, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(setup.VehicleId.ToString(), e.Payload, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("@example.com", e.Payload, StringComparison.Ordinal);
        });
        Assert.Contains("\"method\": \"MbWay\"", events[2].Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Paying_an_open_session_is_a_conflict_and_writes_nothing()
    {
        var setup = await SetUpAsync(new TariffRulesDto(1.20m, 0));
        var started = await StartAsync(setup);

        var response = await Client.PostAsJsonAsync(
            $"/api/sessions/{started.Id}/pay", new PaySession(PaymentMethod.Card), RelationalFixture.Json, Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(0, await fixture.WithDbAsync(db => db.Payments.CountAsync(p => p.SessionId == started.Id, Ct)));
        Assert.Equal(["SessionStarted"], (await OutboxAsync(started.Id)).Select(e => e.Type));
        var session = await Client.GetFromJsonAsync<SessionDto>($"/api/sessions/{started.Id}", RelationalFixture.Json, Ct);
        Assert.Equal(ParkingSessionStatus.Open, session!.Status);
    }

    /// <summary>
    /// Two concurrent closes over HTTP: exactly one succeeds. The loser gets 409 whether it fails on the
    /// concurrency token (both read Open) or reads the already-closed session (invalid transition).
    /// </summary>
    [Fact]
    public async Task Two_concurrent_closes_of_the_same_session_one_succeeds_and_the_other_gets_409()
    {
        var setup = await SetUpAsync(new TariffRulesDto(1.20m, 0));
        var started = await StartAsync(setup);

        var responses = await Task.WhenAll(
            Client.PostAsync($"/api/sessions/{started.Id}/close", null, Ct),
            Client.PostAsync($"/api/sessions/{started.Id}/close", null, Ct));

        Assert.Equal(
            [HttpStatusCode.OK, HttpStatusCode.Conflict],
            responses.Select(r => r.StatusCode).Order());
        Assert.Equal(1, (await OutboxAsync(started.Id)).Count(e => e.Type == nameof(SessionClosed)));
    }

    /// <summary>
    /// Deterministic version of the race: both writers read the open session before either commits, so the
    /// second one must fail on the concurrency token (xmin), and its event must not reach the outbox.
    /// </summary>
    [Fact]
    public async Task Concurrent_close_that_loses_on_the_concurrency_token_leaves_no_outbox_event()
    {
        var setup = await SetUpAsync(new TariffRulesDto(1.20m, 0));
        var started = await StartAsync(setup);

        await using var scopeA = fixture.Services.CreateAsyncScope();
        await using var scopeB = fixture.Services.CreateAsyncScope();
        var a = await scopeA.ServiceProvider.GetRequiredService<IParkingSessionRepository>().GetAsync(started.Id, Ct);
        var b = await scopeB.ServiceProvider.GetRequiredService<IParkingSessionRepository>().GetAsync(started.Id, Ct);

        a!.Close(DateTimeOffset.UtcNow, setup.TariffId, 1.00m);
        await scopeA.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync(Ct);

        b!.Close(DateTimeOffset.UtcNow, setup.TariffId, 9.99m);
        await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => scopeB.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync(Ct));

        var closedEvents = (await OutboxAsync(started.Id)).Where(e => e.Type == nameof(SessionClosed)).ToList();
        var single = Assert.Single(closedEvents);
        Assert.Contains("\"cost\": 1.00", single.Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Close_with_a_stale_version_is_a_conflict()
    {
        var setup = await SetUpAsync(new TariffRulesDto(1.20m, 0));
        var started = await StartAsync(setup);

        var response = await Client.PostAsJsonAsync(
            $"/api/sessions/{started.Id}/close", new CloseSession(started.Version + 1), RelationalFixture.Json, Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(["SessionStarted"], (await OutboxAsync(started.Id)).Select(e => e.Type));
    }

    [Fact]
    public async Task Close_without_a_tariff_in_force_is_a_conflict_and_the_session_stays_open()
    {
        var setup = await SetUpAsync(new TariffRulesDto(1.20m, 0), validFrom: DateTimeOffset.UtcNow.AddDays(30));
        var started = await StartAsync(setup);

        var response = await Client.PostAsync($"/api/sessions/{started.Id}/close", null, Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var session = await Client.GetFromJsonAsync<SessionDto>($"/api/sessions/{started.Id}", RelationalFixture.Json, Ct);
        Assert.Equal(ParkingSessionStatus.Open, session!.Status);
    }

    [Fact]
    public async Task Second_open_session_on_the_same_spot_is_a_conflict()
    {
        var setup = await SetUpAsync(new TariffRulesDto(1.20m, 0));
        await StartAsync(setup);

        var response = await Client.PostAsJsonAsync(
            "/api/sessions", new StartSession(setup.SpotId, setup.VehicleId, setup.UserId), RelationalFixture.Json, Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Vehicle_of_another_user_is_rejected()
    {
        var setup = await SetUpAsync(new TariffRulesDto(1.20m, 0));
        var (otherUser, _) = await fixture.CreateUserWithVehicleAsync(Ct);

        var response = await Client.PostAsJsonAsync(
            "/api/sessions", new StartSession(setup.SpotId, setup.VehicleId, otherUser.Id), RelationalFixture.Json, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Unknown_session_or_spot_is_not_found()
    {
        var setup = await SetUpAsync(new TariffRulesDto(1.20m, 0));

        var close = await Client.PostAsync($"/api/sessions/{Guid.NewGuid()}/close", null, Ct);
        var start = await Client.PostAsJsonAsync(
            "/api/sessions", new StartSession(Guid.NewGuid(), setup.VehicleId, setup.UserId), RelationalFixture.Json, Ct);

        Assert.Equal(HttpStatusCode.NotFound, close.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, start.StatusCode);
    }

    // ---- helpers ----

    internal sealed record Setup(Guid SpotId, Guid UserId, Guid VehicleId, Guid TariffId);

    private async Task<Setup> SetUpAsync(TariffRulesDto rules, DateTimeOffset? validFrom = null)
    {
        var zone = RelationalFixture.NewZone();
        var tariff = await fixture.CreateTariffAsync(zone, rules, validFrom, Ct);
        var facility = await fixture.CreateFacilityAsync(zone: zone, ct: Ct);
        var spot = await fixture.CreateSpotAsync(facility.Id, ct: Ct);
        var (user, vehicle) = await fixture.CreateUserWithVehicleAsync(Ct);
        return new Setup(spot.Id, user.Id, vehicle.Id, tariff.Id);
    }

    private async Task<SessionDto> StartAsync(Setup setup)
    {
        var response = await Client.PostAsJsonAsync(
            "/api/sessions", new StartSession(setup.SpotId, setup.VehicleId, setup.UserId), RelationalFixture.Json, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<SessionDto>(RelationalFixture.Json, Ct))!;
    }

    private Task<List<OutboxMessage>> OutboxAsync(Guid sessionId) =>
        fixture.WithDbAsync(db => db.OutboxMessages.AsNoTracking()
            .Where(m => m.AggregateId == sessionId)
            .OrderBy(m => m.OccurredAt).ThenBy(m => m.Id)
            .ToListAsync(Ct));
}
