using SmartParking.Api.Tests.Fixtures;
using SmartParking.Domain.Facilities;
using SmartParking.Infrastructure.Relational;

namespace SmartParking.Api.Tests.Features.Schema;

/// <summary>The <c>Initial</c> migration and the shape of the schema (spec 001, AC-1 to AC-3).</summary>
[Collection(ApiCollectionDefinition.Name)]
public sealed class InitialMigrationTests(ApiFixture fixture)
{
    private static readonly string[] ExpectedTables =
    [
        "audit_entries", "facility_opening_hours", "operators", "outbox_messages", "parking_facilities",
        "parking_sessions", "parking_spots", "payment_methods", "payments", "reservations", "sensors",
        "tariffs", "users", "vehicles", "zones",
    ];

    [Fact]
    public async Task Migrate_EmptyDatabase_CreatesSchemaExtensionsRoleAndReferenceOperator_AC1()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        var database = await fixture.Databases.CreateIsolatedDatabaseAsync(cancellationToken, migrate: false);
        var owner = database.OwnerConnectionString;

        // Act
        await RelationalDatabase.MigrateAsync(owner, cancellationToken);

        // Assert
        var tables = await DbProbe.ColumnAsync(
            owner, "SELECT tablename::text FROM pg_tables WHERE schemaname = 'public'", cancellationToken);
        Assert.Subset(tables.ToHashSet(), ExpectedTables.ToHashSet());

        var extensions = await DbProbe.ColumnAsync(
            owner, "SELECT extname::text FROM pg_extension WHERE extname IN ('postgis', 'btree_gist') ORDER BY 1", cancellationToken);
        Assert.Equal(["btree_gist", "postgis"], extensions);

        var groupCanLogin = await DbProbe.ScalarAsync<bool>(
            owner, "SELECT rolcanlogin FROM pg_roles WHERE rolname = $1", cancellationToken, RelationalDatabase.ApplicationRole);
        Assert.False(groupCanLogin);

        var operatorName = await DbProbe.ScalarAsync<string>(
            owner, "SELECT name FROM operators WHERE id = $1", cancellationToken, Operator.ReferenceOperatorId);
        Assert.Equal(Operator.ReferenceOperatorName, operatorName);
    }

    [Fact]
    public async Task FacilitiesLocation_Inspected_IsGeographyPointWithGistIndex_AC2()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        var owner = fixture.Databases.OwnerConnectionString;

        // Act
        var columnType = await DbProbe.ScalarAsync<string>(
            owner,
            """
            SELECT format_type(atttypid, atttypmod) FROM pg_attribute
            WHERE attrelid = 'parking_facilities'::regclass AND attname = 'location'
            """,
            cancellationToken);
        var gistIndexes = await DbProbe.ScalarAsync<long>(
            owner,
            """
            SELECT count(*) FROM pg_indexes
            WHERE tablename = 'parking_facilities' AND indexdef LIKE '%USING gist (location)%'
            """,
            cancellationToken);

        // Assert
        Assert.Equal("geography(Point,4326)", columnType);
        Assert.Equal(1, gistIndexes);
    }

    [Fact]
    public async Task ParkingSpots_Inspected_HasNoOccupancyColumnOnlyStatus_AC3()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        var owner = fixture.Databases.OwnerConnectionString;

        // Act
        var columns = await DbProbe.ColumnAsync(
            owner,
            "SELECT column_name::text FROM information_schema.columns WHERE table_name = 'parking_spots' ORDER BY column_name",
            cancellationToken);
        var statusCheck = await DbProbe.ScalarAsync<string>(
            owner,
            "SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conname = 'ck_parking_spots_status'",
            cancellationToken);

        // Assert
        Assert.Equal(["code", "facility_id", "id", "status", "type", "zone_id"], columns);
        Assert.DoesNotContain(columns, column => column.Contains("occup", StringComparison.OrdinalIgnoreCase)
            || column.Contains("free", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("'Active'", statusCheck, StringComparison.Ordinal);
        Assert.Contains("'Inactive'", statusCheck, StringComparison.Ordinal);
        Assert.DoesNotContain("Occupied", statusCheck, StringComparison.Ordinal);
    }
}
