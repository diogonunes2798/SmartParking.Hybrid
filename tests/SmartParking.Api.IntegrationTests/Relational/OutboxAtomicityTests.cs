using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SmartParking.Application.Configuration;
using SmartParking.Application.Sessions;
using SmartParking.Domain.Sessions;

namespace SmartParking.Api.IntegrationTests.Relational;

/// <summary>
/// Transactional outbox (spec §5, ADR-0004): closing a session writes the session and its event, or
/// neither. The failure is injected inside the database transaction with a PostgreSQL trigger that raises
/// an error for this session only, once on the outbox insert and once on the session update. Together the
/// two tests cover a failure on either side of the batch, whatever order EF Core sends the statements in.
/// </summary>
[Collection(RelationalTestGroup.Name)]
public sealed class OutboxAtomicityTests(RelationalFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Failure_writing_the_outbox_event_rolls_back_the_session_close()
    {
        var sessionId = await StartSessionAsync();

        await using (await FailingTriggerAsync("outbox_messages", "INSERT", "aggregate_id", sessionId))
        {
            var response = await fixture.Client.PostAsync($"/api/sessions/{sessionId}/close", null, Ct);
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        }

        await AssertNothingWrittenAsync(sessionId);
    }

    [Fact]
    public async Task Failure_writing_the_session_rolls_back_the_outbox_event()
    {
        var sessionId = await StartSessionAsync();

        await using (await FailingTriggerAsync("parking_sessions", "UPDATE", "id", sessionId))
        {
            var response = await fixture.Client.PostAsync($"/api/sessions/{sessionId}/close", null, Ct);
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        }

        await AssertNothingWrittenAsync(sessionId);
    }

    /// <summary>After the failed close: still open, only the start event, no audit row of the close.</summary>
    private async Task AssertNothingWrittenAsync(Guid sessionId)
    {
        var session = await fixture.WithDbAsync(db => db.Sessions.AsNoTracking().SingleAsync(s => s.Id == sessionId, Ct));
        Assert.Equal(ParkingSessionStatus.Open, session.Status);
        Assert.Null(session.EndedAt);

        var events = await fixture.WithDbAsync(db => db.OutboxMessages.AsNoTracking()
            .Where(m => m.AggregateId == sessionId).Select(m => m.Type).ToListAsync(Ct));
        Assert.Equal(["SessionStarted"], events);

        var audit = await fixture.WithDbAsync(db => db.AuditLog.AsNoTracking()
            .Where(a => a.EntityId == sessionId).Select(a => a.Operation).ToListAsync(Ct));
        Assert.Equal(["Created"], audit);

        // And the close still works once the failure is gone: nothing was left half-written or locked.
        var retry = await fixture.Client.PostAsync($"/api/sessions/{sessionId}/close", null, Ct);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
    }

    private async Task<Guid> StartSessionAsync()
    {
        var zone = RelationalFixture.NewZone();
        await fixture.CreateTariffAsync(zone, new TariffRulesDto(1.20m, 0), ct: Ct);
        var facility = await fixture.CreateFacilityAsync(zone: zone, ct: Ct);
        var spot = await fixture.CreateSpotAsync(facility.Id, ct: Ct);
        var (user, vehicle) = await fixture.CreateUserWithVehicleAsync(Ct);

        var response = await fixture.Client.PostAsJsonAsync(
            "/api/sessions", new StartSession(spot.Id, vehicle.Id, user.Id), RelationalFixture.Json, Ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<SessionDto>(RelationalFixture.Json, Ct))!.Id;
    }

    /// <summary>
    /// Installs a trigger that raises an error when <paramref name="operation"/> on <paramref name="table"/>
    /// touches the row whose <paramref name="column"/> is <paramref name="id"/>; dropped on dispose.
    /// Only this test's row is affected, so other tests sharing the database are not disturbed.
    /// </summary>
    private async Task<IAsyncDisposable> FailingTriggerAsync(string table, string operation, string column, Guid id)
    {
        var name = $"test_fail_{Guid.NewGuid():N}";
        var dataSource = fixture.Services.GetRequiredService<NpgsqlDataSource>();
        await using (var create = dataSource.CreateCommand($"""
            CREATE FUNCTION {name}() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW.{column} = '{id}'::uuid THEN
                    RAISE EXCEPTION 'simulated failure in the middle of the transaction';
                END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER {name} BEFORE {operation} ON {table} FOR EACH ROW EXECUTE FUNCTION {name}();
            """))
        {
            await create.ExecuteNonQueryAsync(Ct);
        }

        return new DropTrigger(dataSource, name, table);
    }

    private sealed class DropTrigger(NpgsqlDataSource dataSource, string name, string table) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await using var drop = dataSource.CreateCommand(
                $"DROP TRIGGER {name} ON {table}; DROP FUNCTION {name}();");
            await drop.ExecuteNonQueryAsync();
        }
    }
}
