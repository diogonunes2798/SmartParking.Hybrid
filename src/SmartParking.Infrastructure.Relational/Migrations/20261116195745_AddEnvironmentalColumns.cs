using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartParking.Infrastructure.Relational.Migrations
{
    /// <summary>
    /// UC-05 in the monolithic variant (spec §4 and §7, ADR-0012): the second generation of sensors adds
    /// <c>temperature</c>, <c>airQuality</c> and <c>noise</c>, and an explicit relational schema needs a migration to store
    /// them. This is the term of comparison of RNF-04 (Cap. 6.5): the hybrid variant receives the same fields in its
    /// extensible <c>payload</c> without any migration (ADR-0011).
    /// <para>
    /// Nullable columns without a default: since PostgreSQL 11, <c>ADD COLUMN</c> then changes only the catalogue and does
    /// not rewrite the rows, on the partitioned table and on every partition. It still needs an ACCESS EXCLUSIVE lock on all
    /// of them, so it waits for the running inserts and, while it waits, blocks the new ones behind it. <c>lock_timeout</c>
    /// bounds that stall to 5 s: if the lock is not obtained in time the migration fails (and can be retried) instead of
    /// stopping the ingestion indefinitely. The measurement of Phase 8 (diary) records what this costs under load.
    /// </para>
    /// <para>
    /// Apply it on its own, in the middle of a run: <c>dotnet ef database update AddMonolithicSchema</c> before the run, then
    /// <c>dotnet ef database update AddEnvironmentalColumns</c>. The API starts storing the readings without a restart
    /// (<c>EnvironmentalColumnsProbe</c>).
    /// </para>
    /// </summary>
    public partial class AddEnvironmentalColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // SET LOCAL: only for the migration's own transaction (EF Core runs each migration in one).
            migrationBuilder.Sql("SET LOCAL lock_timeout = '5s';");
            migrationBuilder.Sql(
                """
                ALTER TABLE monolith.occupancy_events
                    ADD COLUMN temperature double precision,
                    ADD COLUMN air_quality double precision,
                    ADD COLUMN noise       double precision;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("SET LOCAL lock_timeout = '5s';");
            migrationBuilder.Sql(
                """
                ALTER TABLE monolith.occupancy_events
                    DROP COLUMN temperature,
                    DROP COLUMN air_quality,
                    DROP COLUMN noise;
                """);
        }
    }
}
