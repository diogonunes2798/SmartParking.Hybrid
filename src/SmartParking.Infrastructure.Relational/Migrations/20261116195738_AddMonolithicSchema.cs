using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartParking.Infrastructure.Relational.Migrations
{
    /// <summary>
    /// Phase 8, monolithic variant (spec §7, ADR-0012). Hand-written SQL, outside the EF Core model (the snapshot does not
    /// change): EF Core cannot express declarative partitioning nor PostGIS expression indexes. The objects exist in both
    /// modes, so that there is a single migration history; the hybrid variant never reads them.
    /// <list type="number">
    /// <item>PostGIS in its own schema <c>postgis</c> (not in <c>public</c>, where its <c>spatial_ref_sys</c> table would mix
    /// with the application tables). Every call is schema-qualified (<c>postgis.ST_DWithin</c>), so no search_path is needed.
    /// <c>CREATE EXTENSION postgis</c> needs a superuser (the development and test containers' user).</item>
    /// <item><c>monolith.occupancy_events</c>, <c>PARTITION BY RANGE ("timestamp")</c>, one partition per UTC month plus DEFAULT.
    /// The primary key must contain the partition key: <c>(event_id, "timestamp")</c>. The two secondary indexes are those of
    /// the MongoDB log (ADR-0007, ADR-0010), so that both variants pay for the same indexes on every insert.</item>
    /// <item><c>monolith.ensure_occupancy_partitions(from_month, months)</c>, called here (previous month to three months
    /// ahead) and daily by the Workers.</item>
    /// <item><c>monolith.spot_state</c>: the current state of each spot, rewritten on every event. <c>fillfactor = 70</c> leaves
    /// room on each page for HOT updates (no index entry per update, less bloat). No foreign key to <c>parking_spots</c>:
    /// events of unknown spots are accepted, as by the hybrid log, and ignored by the availability query (inner join).</item>
    /// <item>GiST expression index on the facilities' location as <c>geography</c>, over the two <c>double precision</c> columns
    /// of ADR-0003: the table does not change.</item>
    /// </list>
    /// </summary>
    public partial class AddMonolithicSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("CREATE SCHEMA IF NOT EXISTS postgis;");
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS postgis SCHEMA postgis;");
            migrationBuilder.Sql("CREATE SCHEMA monolith;");

            migrationBuilder.Sql(
                """
                CREATE TABLE monolith.occupancy_events (
                    event_id     uuid        NOT NULL,
                    sensor_id    uuid        NOT NULL,
                    spot_id      uuid        NOT NULL,
                    facility_id  uuid        NOT NULL,
                    type         smallint    NOT NULL CONSTRAINT ck_occupancy_events_type CHECK (type IN (1, 2)),
                    "timestamp"  timestamptz NOT NULL,
                    received_at  timestamptz NOT NULL,
                    stored_at    timestamptz NOT NULL,
                    CONSTRAINT pk_occupancy_events PRIMARY KEY (event_id, "timestamp")
                ) PARTITION BY RANGE ("timestamp");
                """);

            // Indexes on the partitioned table: PostgreSQL creates them on every partition, present and future.
            migrationBuilder.Sql(
                """
                CREATE INDEX ix_occupancy_events_facility_id_timestamp
                    ON monolith.occupancy_events (facility_id, "timestamp");
                """);
            migrationBuilder.Sql(
                """
                CREATE INDEX ix_occupancy_events_facility_id_spot_id_timestamp_event_id
                    ON monolith.occupancy_events (facility_id, spot_id, "timestamp" DESC, event_id DESC);
                """);

            migrationBuilder.Sql("CREATE TABLE monolith.occupancy_events_default PARTITION OF monolith.occupancy_events DEFAULT;");

            // Idempotent and safe to run concurrently: an existing partition is skipped; a month that already has rows in
            // DEFAULT cannot get its partition (check_violation) and is skipped with a WARNING instead of failing the run.
            // CREATE TABLE … PARTITION OF takes ACCESS EXCLUSIVE on the parent (and scans DEFAULT): behind a long reader it
            // would queue every insert behind it. lock_timeout = 2 s bounds that stall; partitions are created months ahead,
            // so a skipped day is harmless (review of Phase 8, migrations finding 2).
            migrationBuilder.Sql(
                """
                CREATE FUNCTION monolith.ensure_occupancy_partitions(from_month date, months integer)
                RETURNS integer
                LANGUAGE plpgsql
                SET lock_timeout = '2s'
                AS $$
                DECLARE
                    first_month date := date_trunc('month', from_month)::date;
                    month_start date;
                    partition_name text;
                    created integer := 0;
                BEGIN
                    IF months IS NULL OR months < 1 THEN
                        RAISE EXCEPTION 'months must be at least 1, got %', months;
                    END IF;

                    FOR i IN 0 .. months - 1 LOOP
                        month_start := (first_month + make_interval(months => i))::date;
                        partition_name := format('occupancy_events_y%sm%s', to_char(month_start, 'YYYY'), to_char(month_start, 'MM'));
                        IF to_regclass('monolith.' || partition_name) IS NULL THEN
                            BEGIN
                                EXECUTE format(
                                    'CREATE TABLE monolith.%I PARTITION OF monolith.occupancy_events FOR VALUES FROM (%L) TO (%L)',
                                    partition_name,
                                    month_start::timestamp AT TIME ZONE 'UTC',
                                    (month_start + interval '1 month')::timestamp AT TIME ZONE 'UTC');
                                created := created + 1;
                            EXCEPTION
                                WHEN check_violation THEN
                                    RAISE WARNING 'partition % not created: the DEFAULT partition already has rows in that month', partition_name;
                                WHEN duplicate_table THEN
                                    NULL; -- created meanwhile by another Workers instance
                                WHEN lock_not_available THEN
                                    RAISE WARNING 'partition % not created: lock not obtained in 2 s (retried on the next run)', partition_name;
                            END;
                        END IF;
                    END LOOP;

                    RETURN created;
                END;
                $$;
                """);

            migrationBuilder.Sql(
                """
                SELECT monolith.ensure_occupancy_partitions(
                    (date_trunc('month', now() AT TIME ZONE 'UTC') - interval '1 month')::date, 5);
                """);

            migrationBuilder.Sql(
                """
                CREATE TABLE monolith.spot_state (
                    spot_id     uuid        NOT NULL CONSTRAINT pk_spot_state PRIMARY KEY,
                    type        smallint    NOT NULL CONSTRAINT ck_spot_state_type CHECK (type IN (1, 2)),
                    event_id    uuid        NOT NULL,
                    "timestamp" timestamptz NOT NULL,
                    updated_at  timestamptz NOT NULL
                ) WITH (fillfactor = 70);
                """);

            // Same expression as MonolithicSchema.FacilityGeography (the planner matches expression indexes textually).
            migrationBuilder.Sql(
                """
                CREATE INDEX ix_parking_facilities_geography ON parking_facilities
                    USING gist ((postgis.ST_SetSRID(postgis.ST_MakePoint(longitude, latitude), 4326)::postgis.geography));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_parking_facilities_geography;");
            migrationBuilder.Sql("DROP SCHEMA monolith CASCADE;");
            migrationBuilder.Sql("DROP EXTENSION IF EXISTS postgis;");
            migrationBuilder.Sql("DROP SCHEMA IF EXISTS postgis;");
        }
    }
}
