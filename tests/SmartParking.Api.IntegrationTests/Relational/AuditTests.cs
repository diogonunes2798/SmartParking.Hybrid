using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmartParking.Application.Configuration;
using SmartParking.Domain.Facilities;
using SmartParking.Domain.Users;
using SmartParking.Infrastructure.Relational;
using SmartParking.Infrastructure.Relational.Auditing;

namespace SmartParking.Api.IntegrationTests.Relational;

/// <summary>RNF-06: every change to relational data leaves an audit row, in the same transaction.</summary>
[Collection(RelationalTestGroup.Name)]
public sealed class AuditTests(RelationalFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The manual Swagger scenario of Phase 2a: one facility, two spots, one sensor → four audit rows.</summary>
    [Fact]
    public async Task Creating_a_facility_two_spots_and_a_sensor_writes_one_audit_row_each()
    {
        var facility = await fixture.CreateFacilityAsync(ct: Ct);
        var spot1 = await fixture.CreateSpotAsync(facility.Id, ct: Ct);
        var spot2 = await fixture.CreateSpotAsync(facility.Id, ct: Ct);
        var sensor = await fixture.CreateSensorAsync(spot1.Id, Ct);

        var ids = new[] { facility.Id, spot1.Id, spot2.Id, sensor.Id };
        var rows = await AuditRowsAsync(ids);

        Assert.Equal(4, rows.Count);
        Assert.All(rows, r =>
        {
            Assert.Equal("Created", r.Operation);
            Assert.Equal(DependencyInjection.AnonymousActor, r.Actor);
        });
        Assert.Equal(
            ["ParkingFacility", "ParkingSpot", "ParkingSpot", "Sensor"],
            rows.OrderBy(r => r.OccurredAt).ThenBy(r => r.Id).Select(r => r.EntityType));

        using var changes = JsonDocument.Parse(rows.Single(r => r.EntityId == facility.Id).Changes);
        Assert.Equal(facility.Name, changes.RootElement.GetProperty("name").GetString());
        Assert.Equal(40.2033, changes.RootElement.GetProperty("location.latitude").GetDouble());
        Assert.Equal("MultiStorey", changes.RootElement.GetProperty("type").GetString());
        Assert.False(changes.RootElement.TryGetProperty("version", out _), "xmin must not be audited");
    }

    [Fact]
    public async Task Update_records_only_changed_fields_with_old_and_new_values_and_delete_records_old_values()
    {
        var facility = await fixture.CreateFacilityAsync(name: "Antes", ct: Ct);
        var update = new UpdateFacility(
            "Depois", RelationalFixture.Coimbra, 120, FacilityType.MultiStorey, "ISEC", "Centro", facility.Version);
        (await fixture.Client.PutAsJsonAsync($"/api/facilities/{facility.Id}", update, RelationalFixture.Json, Ct))
            .EnsureSuccessStatusCode();
        (await fixture.Client.DeleteAsync($"/api/facilities/{facility.Id}", Ct)).EnsureSuccessStatusCode();

        var rows = (await AuditRowsAsync([facility.Id])).OrderBy(r => r.OccurredAt).ToList();

        Assert.Equal(["Created", "Updated", "Deleted"], rows.Select(r => r.Operation));

        using var updated = JsonDocument.Parse(rows[1].Changes);
        var name = Assert.Single(updated.RootElement.EnumerateObject());
        Assert.Equal("name", name.Name);
        Assert.Equal("Antes", name.Value.GetProperty("old").GetString());
        Assert.Equal("Depois", name.Value.GetProperty("new").GetString());

        using var deleted = JsonDocument.Parse(rows[2].Changes);
        Assert.Equal("Depois", deleted.RootElement.GetProperty("name").GetString());
    }

    /// <summary>
    /// Review of Phase 2a, finding 6: complex properties are audited with a dotted path, both the ones mapped
    /// to columns (location, validity) and the one mapped to a jsonb document (tariff rules).
    /// </summary>
    [Fact]
    public async Task Complex_and_json_properties_are_audited_with_dotted_names()
    {
        var facility = await fixture.CreateFacilityAsync(location: new GeoJsonPoint("Point", [-8.41, 40.20]), ct: Ct);
        var move = new UpdateFacility(
            facility.Name, new GeoJsonPoint("Point", [-8.41, 40.25]), facility.Capacity, facility.Type,
            facility.Operator, facility.Zone, facility.Version);
        (await fixture.Client.PutAsJsonAsync($"/api/facilities/{facility.Id}", move, RelationalFixture.Json, Ct))
            .EnsureSuccessStatusCode();

        var tariffResponse = await fixture.Client.PostAsJsonAsync(
            "/api/tariffs",
            new CreateTariff("Centro", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), new TariffRulesDto(1.20m, 15, 9.60m)),
            RelationalFixture.Json,
            Ct);
        var tariff = await tariffResponse.Content.ReadFromJsonAsync<TariffDto>(RelationalFixture.Json, Ct);

        var facilityRows = (await AuditRowsAsync([facility.Id])).OrderBy(r => r.OccurredAt).ToList();
        using (var moved = JsonDocument.Parse(facilityRows.Single(r => r.Operation == "Updated").Changes))
        {
            var latitude = Assert.Single(moved.RootElement.EnumerateObject());
            Assert.Equal("location.latitude", latitude.Name);
            Assert.Equal(40.20, latitude.Value.GetProperty("old").GetDouble());
            Assert.Equal(40.25, latitude.Value.GetProperty("new").GetDouble());
        }

        using var created = JsonDocument.Parse(Assert.Single(await AuditRowsAsync([tariff!.Id])).Changes);
        Assert.Equal(1.20m, created.RootElement.GetProperty("rules.hourlyRate").GetDecimal());
        Assert.Equal(15, created.RootElement.GetProperty("rules.freeMinutes").GetInt32());
        Assert.Equal(9.60m, created.RootElement.GetProperty("rules.dailyCap").GetDecimal());
        Assert.True(created.RootElement.TryGetProperty("validity.validFrom", out _), "validity.validFrom missing");
    }

    /// <summary>Change and audit row share the transaction: a rejected change leaves no audit row.</summary>
    [Fact]
    public async Task Failed_change_leaves_no_audit_row()
    {
        var facility = await fixture.CreateFacilityAsync(ct: Ct);
        await fixture.CreateSpotAsync(facility.Id, "SAME", Ct);
        var spotRowsBefore = await CountAuditRowsAsync("ParkingSpot");

        var duplicate = await fixture.Client.PostAsJsonAsync(
            $"/api/facilities/{facility.Id}/spots", new CreateSpot("SAME", VehicleType.Car), RelationalFixture.Json, Ct);

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal(spotRowsBefore, await CountAuditRowsAsync("ParkingSpot"));
    }

    /// <summary>GDPR: personal data is masked in the audit log; non-personal fields stay readable.</summary>
    [Fact]
    public async Task Personal_data_is_masked_in_the_audit_log()
    {
        var user = User.Create("Maria Silva", $"maria.{Guid.NewGuid():N}@example.com");
        var vehicle = Vehicle.Create(user.Id, "AA-00-BB", VehicleType.Car);
        await fixture.WithDbAsync(async db =>
        {
            db.Users.Add(user);
            db.Vehicles.Add(vehicle);
            return await db.SaveChangesAsync(Ct);
        });

        var rows = await AuditRowsAsync([user.Id, vehicle.Id]);
        var raw = string.Join('\n', rows.Select(r => r.Changes));

        Assert.DoesNotContain("Maria", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("example.com", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("AA00BB", raw, StringComparison.Ordinal);

        using var vehicleChanges = JsonDocument.Parse(rows.Single(r => r.EntityId == vehicle.Id).Changes);
        Assert.Equal(AuditSaveChangesInterceptor.Masked, vehicleChanges.RootElement.GetProperty("licensePlate").GetString());
        Assert.Equal("Car", vehicleChanges.RootElement.GetProperty("type").GetString());
    }

    private Task<List<AuditEntry>> AuditRowsAsync(Guid[] entityIds) =>
        fixture.WithDbAsync(db => db.AuditLog.AsNoTracking().Where(a => entityIds.Contains(a.EntityId)).ToListAsync(Ct));

    private Task<int> CountAuditRowsAsync(string entityType) =>
        fixture.WithDbAsync(db => db.AuditLog.CountAsync(a => a.EntityType == entityType, Ct));
}
