using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartParking.Infrastructure.Relational.Migrations
{
    /// <inheritdoc />
    public partial class AddOccupancyHourlySummary : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "analytics");

            migrationBuilder.CreateTable(
                name: "aggregation_checkpoints",
                schema: "analytics",
                columns: table => new
                {
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    position = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_aggregation_checkpoints", x => x.name);
                });

            migrationBuilder.CreateTable(
                name: "occupancy_hourly",
                schema: "analytics",
                columns: table => new
                {
                    facility_id = table.Column<Guid>(type: "uuid", nullable: false),
                    hour_start = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    zone = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    spot_count = table.Column<int>(type: "integer", nullable: false),
                    occupied_seconds = table.Column<long>(type: "bigint", nullable: false),
                    available_seconds = table.Column<long>(type: "bigint", nullable: false),
                    event_count = table.Column<int>(type: "integer", nullable: false),
                    aggregated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_occupancy_hourly", x => new { x.hour_start, x.facility_id });
                    table.CheckConstraint("ck_occupancy_hourly_available", "available_seconds = spot_count::bigint * 3600");
                    table.CheckConstraint("ck_occupancy_hourly_events", "event_count >= 0");
                    table.CheckConstraint("ck_occupancy_hourly_occupied", "occupied_seconds BETWEEN 0 AND available_seconds");
                    table.CheckConstraint("ck_occupancy_hourly_spot_count", "spot_count >= 0");
                });

            migrationBuilder.CreateIndex(
                name: "ix_occupancy_hourly_zone_hour_start",
                schema: "analytics",
                table: "occupancy_hourly",
                columns: new[] { "zone", "hour_start" })
                .Annotation("Npgsql:IndexInclude", new[] { "event_count", "occupied_seconds", "available_seconds" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "aggregation_checkpoints",
                schema: "analytics");

            migrationBuilder.DropTable(
                name: "occupancy_hourly",
                schema: "analytics");

            // EnsureSchema has no automatic counterpart: without this, Down() would leave an empty schema behind.
            migrationBuilder.DropSchema(name: "analytics");
        }
    }
}
