using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartParking.Infrastructure.Relational.Migrations
{
    /// <summary>
    /// Phase 10 (RNF-05, ADR-0013): retention of the raw occupancy events.
    /// <list type="bullet">
    /// <item><c>analytics.aggregated_hours</c>: hours covered by the UC-04 aggregation (a day is removed only when covered);</item>
    /// <item><c>analytics.retention_archive</c>: manifest of the archive files, one row per UTC day;</item>
    /// <item><c>analytics.retention_positions</c>: <c>archived_until</c> and <c>purge_horizon</c>, only forward;</item>
    /// <item><c>analytics.spot_state_snapshot</c>: last event of each spot before the purge horizon;</item>
    /// <item>functions for the daily partitions of <c>monolith.occupancy_events</c>.</item>
    /// </list>
    /// </summary>
    public partial class AddRetention : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "aggregated_hours",
                schema: "analytics",
                columns: table => new
                {
                    hour_start = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    aggregated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_aggregated_hours", x => x.hour_start);
                });

            migrationBuilder.CreateTable(
                name: "retention_archive",
                schema: "analytics",
                columns: table => new
                {
                    day = table.Column<DateOnly>(type: "date", nullable: false),
                    file = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    events = table.Column<long>(type: "bigint", nullable: false),
                    bytes = table.Column<long>(type: "bigint", nullable: false),
                    sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    archived_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_retention_archive", x => x.day);
                });

            migrationBuilder.CreateTable(
                name: "retention_positions",
                schema: "analytics",
                columns: table => new
                {
                    name = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    position = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_retention_positions", x => x.name);
                });

            migrationBuilder.CreateTable(
                name: "spot_state_snapshot",
                schema: "analytics",
                columns: table => new
                {
                    spot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    facility_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<short>(type: "smallint", nullable: false),
                    timestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    saved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_spot_state_snapshot", x => x.spot_id);
                    table.CheckConstraint("ck_spot_state_snapshot_type", "type IN (1, 2)");
                });

            migrationBuilder.CreateIndex(
                name: "ix_spot_state_snapshot_facility_id",
                schema: "analytics",
                table: "spot_state_snapshot",
                column: "facility_id");

            // Hours aggregated before this migration: an hour with summary rows was aggregated (Phase 6). Hours without any
            // row stay uncovered; retention then waits for aggregate-occupancy on them (safe direction).
            migrationBuilder.Sql(
                """
                INSERT INTO analytics.aggregated_hours (hour_start, aggregated_at)
                SELECT hour_start, max(aggregated_at) FROM analytics.occupancy_hourly GROUP BY hour_start
                ON CONFLICT DO NOTHING;
                """);

            // Daily partitions of the monolithic variant (Monolithic:PartitionInterval = Day, ADR-0013). Same safeguards as
            // ensure_occupancy_partitions (ADR-0012): idempotent, lock_timeout 2 s, a failed day skipped with a WARNING.
            migrationBuilder.Sql(
                """
                CREATE FUNCTION monolith.create_occupancy_day_partition(day date)
                RETURNS integer
                LANGUAGE plpgsql
                AS $$
                DECLARE
                    partition_name text := format('occupancy_events_d%s', to_char(day, 'YYYYMMDD'));
                BEGIN
                    IF to_regclass('monolith.' || partition_name) IS NOT NULL THEN
                        RETURN 0;
                    END IF;
                    EXECUTE format(
                        'CREATE TABLE monolith.%I PARTITION OF monolith.occupancy_events FOR VALUES FROM (%L) TO (%L)',
                        partition_name,
                        day::timestamp AT TIME ZONE 'UTC',
                        (day + 1)::timestamp AT TIME ZONE 'UTC');
                    RETURN 1;
                END;
                $$;
                """);

            // A monthly partition in the way: if it is empty it is dropped (under a lock of the parent) and replaced by the daily partitions of
            // its whole month (so that no day of that month falls into DEFAULT); if it has rows it is kept, with a WARNING.
            migrationBuilder.Sql(
                """
                CREATE FUNCTION monolith.ensure_occupancy_day_partitions(from_day date, days integer)
                RETURNS integer
                LANGUAGE plpgsql
                SET lock_timeout = '2s'
                AS $$
                DECLARE
                    d date;
                    month_start date;
                    month_name text;
                    has_rows boolean;
                    created integer := 0;
                BEGIN
                    IF days IS NULL OR days < 1 THEN
                        RAISE EXCEPTION 'days must be at least 1, got %', days;
                    END IF;

                    FOR i IN 0 .. days - 1 LOOP
                        d := from_day + i;
                        CONTINUE WHEN to_regclass(format('monolith.occupancy_events_d%s', to_char(d, 'YYYYMMDD'))) IS NOT NULL;
                        month_start := date_trunc('month', d)::date;
                        month_name := format('occupancy_events_y%sm%s', to_char(month_start, 'YYYY'), to_char(month_start, 'MM'));
                        BEGIN
                            IF to_regclass('monolith.' || month_name) IS NOT NULL THEN
                                -- The parent first, as the DROP will need it: locking the partition first could deadlock
                                -- with an insert, which locks the parent and then the partition.
                                LOCK TABLE monolith.occupancy_events IN ACCESS EXCLUSIVE MODE;
                                EXECUTE format('SELECT EXISTS (SELECT 1 FROM monolith.%I)', month_name) INTO has_rows;
                                IF has_rows THEN
                                    RAISE WARNING 'daily partition of % not created: the monthly partition % has rows', d, month_name;
                                ELSE
                                    EXECUTE format('DROP TABLE monolith.%I', month_name);
                                    FOR j IN 0 .. ((month_start + interval '1 month')::date - month_start) - 1 LOOP
                                        created := created + monolith.create_occupancy_day_partition(month_start + j);
                                    END LOOP;
                                END IF;
                            ELSE
                                created := created + monolith.create_occupancy_day_partition(d);
                            END IF;
                        EXCEPTION
                            WHEN check_violation THEN
                                RAISE WARNING 'daily partition of % not created: the DEFAULT partition already has rows in that day', d;
                            WHEN duplicate_table THEN
                                NULL; -- created meanwhile by another Workers instance
                            WHEN lock_not_available THEN
                                RAISE WARNING 'daily partition of % not created: lock not obtained in 2 s (retried on the next run)', d;
                        END;
                    END LOOP;

                    RETURN created;
                END;
                $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The daily partitions themselves stay (they may hold events); only the functions go.
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS monolith.ensure_occupancy_day_partitions(date, integer);");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS monolith.create_occupancy_day_partition(date);");

            migrationBuilder.DropTable(
                name: "aggregated_hours",
                schema: "analytics");

            migrationBuilder.DropTable(
                name: "retention_archive",
                schema: "analytics");

            migrationBuilder.DropTable(
                name: "retention_positions",
                schema: "analytics");

            migrationBuilder.DropTable(
                name: "spot_state_snapshot",
                schema: "analytics");
        }
    }
}
