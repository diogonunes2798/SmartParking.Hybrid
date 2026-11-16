using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using SmartParking.Infrastructure.Relational;

namespace SmartParking.Api.IntegrationTests.Relational;

/// <summary>The versioned migrations create the schema, can be rolled back, and match the EF Core model exactly.</summary>
[Collection(RelationalTestGroup.Name)]
public sealed class MigrationTests(RelationalFixture fixture)
{
    private static readonly string[] ExpectedTables =
    [
        "__ef_migrations_history", "audit_log", "outbox_messages", "parking_facilities", "parking_sessions", "parking_spots",
        "payments", "sensors", "tariffs", "users", "vehicles",
    ];

    private static readonly string[] ExpectedAnalyticsTables = ["aggregation_checkpoints", "occupancy_hourly"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Applies every migration to a brand-new database (not the fixture's, which is already migrated),
    /// then rolls them all back: proves both <c>Up()</c> and <c>Down()</c> run on a real PostgreSQL.
    /// </summary>
    [Fact]
    public async Task Migrations_apply_to_a_new_database_and_roll_back_completely()
    {
        var connectionString = new NpgsqlConnectionStringBuilder(fixture.PostgresConnectionString)
        {
            Database = $"migration_check_{Guid.NewGuid():N}",
        }.ConnectionString;
        var options = new DbContextOptionsBuilder<SmartParkingDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable(DependencyInjection.MigrationsHistoryTable))
            .UseSnakeCaseNamingConvention()
            .Options;

        await using var db = new SmartParkingDbContext(options);
        try
        {
            await db.Database.MigrateAsync(Ct);

            Assert.Equal(db.Database.GetMigrations(), await db.Database.GetAppliedMigrationsAsync(Ct));
            Assert.Equivalent(ExpectedTables, await PublicTablesAsync(db), strict: true);
            Assert.Equivalent(ExpectedAnalyticsTables, await AnalyticsTablesAsync(db), strict: true);

            await db.GetService<IMigrator>().MigrateAsync(Migration.InitialDatabase, cancellationToken: Ct);

            Assert.Equal(["__ef_migrations_history"], await PublicTablesAsync(db));
            Assert.Empty(await db.Database
                .SqlQueryRaw<string>("SELECT schema_name AS \"Value\" FROM information_schema.schemata WHERE schema_name = 'analytics'")
                .ToListAsync(Ct));
        }
        finally
        {
            await db.Database.EnsureDeletedAsync(Ct);
        }
    }

    /// <summary>Fails when a mapping changes without a new migration (the schema would drift from the code).</summary>
    [Fact]
    public async Task Model_has_no_changes_missing_from_the_migrations()
    {
        var hasChanges = await fixture.WithDbAsync(db => Task.FromResult(db.Database.HasPendingModelChanges()));

        Assert.False(hasChanges);
    }

    [Fact]
    public async Task Creates_the_expected_tables()
    {
        var tables = await fixture.WithDbAsync(PublicTablesAsync);

        Assert.Equivalent(ExpectedTables, tables, strict: true);
        Assert.Equivalent(ExpectedAnalyticsTables, await fixture.WithDbAsync(AnalyticsTablesAsync), strict: true);
    }

    /// <summary>
    /// UC-04 (D4, ADR-0010): the summary tables live in their own schema and have no foreign key to any operational table.
    /// </summary>
    [Fact]
    public async Task Analytics_tables_have_no_foreign_keys()
    {
        var foreignKeys = await fixture.WithDbAsync(db => db.Database
            .SqlQueryRaw<string>(
                "SELECT constraint_name AS \"Value\" FROM information_schema.table_constraints " +
                "WHERE table_schema = 'analytics' AND constraint_type = 'FOREIGN KEY'")
            .ToListAsync(Ct));

        Assert.Empty(foreignKeys);
    }

    private static Task<List<string>> PublicTablesAsync(SmartParkingDbContext db) => TablesAsync(db, "public");

    private static Task<List<string>> AnalyticsTablesAsync(SmartParkingDbContext db) => TablesAsync(db, "analytics");

    private static Task<List<string>> TablesAsync(SmartParkingDbContext db, string schema) =>
        db.Database
            .SqlQuery<string>($"SELECT table_name AS \"Value\" FROM information_schema.tables WHERE table_schema = {schema}")
            .ToListAsync(Ct);
}
