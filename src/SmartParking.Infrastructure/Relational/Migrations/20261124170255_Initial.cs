using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NetTopologySuite.Geometries;

#nullable disable

namespace SmartParking.Infrastructure.Relational.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:btree_gist", ",,")
                .Annotation("Npgsql:PostgresExtension:postgis", ",,");

            migrationBuilder.CreateTable(
                name: "audit_entries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    actor = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    entity_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    operation = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    before = table.Column<string>(type: "jsonb", nullable: true),
                    after = table.Column<string>(type: "jsonb", nullable: true),
                    trace_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    outbox_message_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_entries", x => x.id);
                    table.CheckConstraint("ck_audit_entries_operation", "operation IN ('Created', 'Updated', 'Deleted')");
                });

            migrationBuilder.CreateTable(
                name: "operators",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_operators", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "outbox_messages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    trace_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    last_error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbox_messages", x => x.id);
                    table.CheckConstraint("ck_outbox_messages_attempts", "attempts >= 0");
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_users", x => x.id);
                    table.CheckConstraint("ck_users_email_lower_case", "email = lower(email)");
                });

            migrationBuilder.CreateTable(
                name: "parking_facilities",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    operator_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    address = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    location = table.Column<Point>(type: "geography (point, 4326)", nullable: false),
                    time_zone = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_parking_facilities", x => x.id);
                    table.CheckConstraint("ck_parking_facilities_status", "status IN ('Active', 'Inactive')");
                    table.ForeignKey(
                        name: "fk_parking_facilities_operators_operator_id",
                        column: x => x.operator_id,
                        principalTable: "operators",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "payment_methods",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    provider_token = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    masked_number = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    expires_on = table.Column<DateOnly>(type: "date", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_payment_methods", x => x.id);
                    table.CheckConstraint("ck_payment_methods_type", "type IN ('Card', 'MbWay')");
                    table.ForeignKey(
                        name: "fk_payment_methods_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "vehicles",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    license_plate = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_vehicles", x => x.id);
                    table.CheckConstraint("ck_vehicles_license_plate_normalised", "license_plate ~ '^[A-Z0-9]+$'");
                    table.ForeignKey(
                        name: "fk_vehicles_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "facility_opening_hours",
                columns: table => new
                {
                    day_of_week = table.Column<short>(type: "smallint", maxLength: 32, nullable: false),
                    facility_id = table.Column<Guid>(type: "uuid", nullable: false),
                    opens_at = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    closes_at = table.Column<TimeOnly>(type: "time without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_facility_opening_hours", x => new { x.facility_id, x.day_of_week });
                    table.CheckConstraint("ck_facility_opening_hours_day_of_week", "day_of_week BETWEEN 0 AND 6");
                    table.CheckConstraint("ck_facility_opening_hours_interval", "opens_at < closes_at");
                    table.ForeignKey(
                        name: "fk_facility_opening_hours_parking_facilities_facility_id",
                        column: x => x.facility_id,
                        principalTable: "parking_facilities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "tariffs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    facility_id = table.Column<Guid>(type: "uuid", nullable: false),
                    valid_from = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    valid_to = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    price_per_hour = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    billing_increment_minutes = table.Column<int>(type: "integer", nullable: false),
                    daily_maximum = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: true),
                    free_minutes = table.Column<int>(type: "integer", nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tariffs", x => x.id);
                    table.CheckConstraint("ck_tariffs_billing_increment_minutes", "billing_increment_minutes BETWEEN 1 AND 60");
                    table.CheckConstraint("ck_tariffs_currency", "currency ~ '^[A-Z]{3}$'");
                    table.CheckConstraint("ck_tariffs_daily_maximum", "daily_maximum IS NULL OR daily_maximum >= 0");
                    table.CheckConstraint("ck_tariffs_free_minutes", "free_minutes >= 0");
                    table.CheckConstraint("ck_tariffs_price_per_hour", "price_per_hour >= 0");
                    table.CheckConstraint("ck_tariffs_validity", "valid_to IS NULL OR valid_to > valid_from");
                    table.ForeignKey(
                        name: "fk_tariffs_parking_facilities_facility_id",
                        column: x => x.facility_id,
                        principalTable: "parking_facilities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "zones",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    facility_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_zones", x => x.id);
                    table.UniqueConstraint("ak_zones_facility_id_id", x => new { x.facility_id, x.id });
                    table.ForeignKey(
                        name: "fk_zones_parking_facilities_facility_id",
                        column: x => x.facility_id,
                        principalTable: "parking_facilities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "parking_spots",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    facility_id = table.Column<Guid>(type: "uuid", nullable: false),
                    zone_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_parking_spots", x => x.id);
                    table.UniqueConstraint("ak_parking_spots_facility_id_id", x => new { x.facility_id, x.id });
                    table.CheckConstraint("ck_parking_spots_status", "status IN ('Active', 'Inactive')");
                    table.CheckConstraint("ck_parking_spots_type", "type IN ('Standard', 'Accessible', 'ElectricVehicle', 'Motorcycle')");
                    table.ForeignKey(
                        name: "fk_parking_spots_parking_facilities_facility_id",
                        column: x => x.facility_id,
                        principalTable: "parking_facilities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_parking_spots_zones_facility_id_zone_id",
                        columns: x => new { x.facility_id, x.zone_id },
                        principalTable: "zones",
                        principalColumns: new[] { "facility_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "reservations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    spot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    vehicle_id = table.Column<Guid>(type: "uuid", nullable: false),
                    starts_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ends_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_reservations", x => x.id);
                    table.CheckConstraint("ck_reservations_interval", "ends_at > starts_at");
                    table.CheckConstraint("ck_reservations_status", "status IN ('Active', 'Cancelled', 'Fulfilled', 'Expired')");
                    table.ForeignKey(
                        name: "fk_reservations_parking_spots_spot_id",
                        column: x => x.spot_id,
                        principalTable: "parking_spots",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_reservations_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_reservations_vehicles_vehicle_id",
                        column: x => x.vehicle_id,
                        principalTable: "vehicles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sensors",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    facility_id = table.Column<Guid>(type: "uuid", nullable: false),
                    hardware_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    model = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    installed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    spot_id = table.Column<Guid>(type: "uuid", nullable: true),
                    zone_id = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sensors", x => x.id);
                    table.CheckConstraint("ck_sensors_kind", "kind IN ('Occupancy', 'Environmental')");
                    table.CheckConstraint("ck_sensors_spot_only_for_occupancy", "kind = 'Occupancy' OR spot_id IS NULL");
                    table.CheckConstraint("ck_sensors_status", "status IN ('Active', 'Inactive')");
                    table.ForeignKey(
                        name: "fk_sensors_parking_facilities_facility_id",
                        column: x => x.facility_id,
                        principalTable: "parking_facilities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sensors_parking_spots_facility_id_spot_id",
                        columns: x => new { x.facility_id, x.spot_id },
                        principalTable: "parking_spots",
                        principalColumns: new[] { "facility_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sensors_zones_facility_id_zone_id",
                        columns: x => new { x.facility_id, x.zone_id },
                        principalTable: "zones",
                        principalColumns: new[] { "facility_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "parking_sessions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    spot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    vehicle_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tariff_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reservation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_parking_sessions", x => x.id);
                    table.CheckConstraint("ck_parking_sessions_amount", "amount IS NULL OR amount >= 0");
                    table.CheckConstraint("ck_parking_sessions_interval", "ended_at IS NULL OR ended_at >= started_at");
                    table.CheckConstraint("ck_parking_sessions_status", "status IN ('Open', 'Closed', 'Paid')");
                    table.ForeignKey(
                        name: "fk_parking_sessions_parking_spots_spot_id",
                        column: x => x.spot_id,
                        principalTable: "parking_spots",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_parking_sessions_reservations_reservation_id",
                        column: x => x.reservation_id,
                        principalTable: "reservations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_parking_sessions_tariffs_tariff_id",
                        column: x => x.tariff_id,
                        principalTable: "tariffs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_parking_sessions_vehicles_vehicle_id",
                        column: x => x.vehicle_id,
                        principalTable: "vehicles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "payments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payment_method_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_payments", x => x.id);
                    table.CheckConstraint("ck_payments_amount", "amount >= 0");
                    table.CheckConstraint("ck_payments_status", "status IN ('Pending', 'Succeeded', 'Failed')");
                    table.ForeignKey(
                        name: "fk_payments_parking_sessions_session_id",
                        column: x => x.session_id,
                        principalTable: "parking_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_payments_payment_methods_payment_method_id",
                        column: x => x.payment_method_id,
                        principalTable: "payment_methods",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "operators",
                columns: new[] { "id", "name" },
                values: new object[] { new Guid("0193a1b0-0000-7000-8000-000000000001"), "Reference Operator" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_entries_entity_type_entity_id",
                table: "audit_entries",
                columns: new[] { "entity_type", "entity_id" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_entries_outbox_message_id",
                table: "audit_entries",
                column: "outbox_message_id");

            migrationBuilder.CreateIndex(
                name: "ix_operators_name",
                table: "operators",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_unpublished",
                table: "outbox_messages",
                column: "occurred_at",
                filter: "published_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_parking_facilities_location",
                table: "parking_facilities",
                column: "location")
                .Annotation("Npgsql:IndexMethod", "gist");

            migrationBuilder.CreateIndex(
                name: "ix_parking_facilities_operator_id_name",
                table: "parking_facilities",
                columns: new[] { "operator_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_parking_sessions_reservation_id",
                table: "parking_sessions",
                column: "reservation_id");

            migrationBuilder.CreateIndex(
                name: "ix_parking_sessions_spot_id",
                table: "parking_sessions",
                column: "spot_id");

            migrationBuilder.CreateIndex(
                name: "ix_parking_sessions_tariff_id",
                table: "parking_sessions",
                column: "tariff_id");

            migrationBuilder.CreateIndex(
                name: "ix_parking_sessions_vehicle_id",
                table: "parking_sessions",
                column: "vehicle_id");

            migrationBuilder.CreateIndex(
                name: "ix_parking_spots_facility_id_code",
                table: "parking_spots",
                columns: new[] { "facility_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_parking_spots_facility_id_zone_id",
                table: "parking_spots",
                columns: new[] { "facility_id", "zone_id" });

            migrationBuilder.CreateIndex(
                name: "ix_payment_methods_user_id",
                table: "payment_methods",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_payments_payment_method_id",
                table: "payments",
                column: "payment_method_id");

            migrationBuilder.CreateIndex(
                name: "ux_payments_session_succeeded",
                table: "payments",
                column: "session_id",
                unique: true,
                filter: "status = 'Succeeded'");

            migrationBuilder.CreateIndex(
                name: "ix_reservations_spot_id",
                table: "reservations",
                column: "spot_id");

            migrationBuilder.CreateIndex(
                name: "ix_reservations_user_id",
                table: "reservations",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_reservations_vehicle_id",
                table: "reservations",
                column: "vehicle_id");

            migrationBuilder.CreateIndex(
                name: "ix_sensors_facility_id_spot_id",
                table: "sensors",
                columns: new[] { "facility_id", "spot_id" });

            migrationBuilder.CreateIndex(
                name: "ix_sensors_facility_id_zone_id",
                table: "sensors",
                columns: new[] { "facility_id", "zone_id" });

            migrationBuilder.CreateIndex(
                name: "ix_sensors_hardware_id",
                table: "sensors",
                column: "hardware_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_sensors_spot_id",
                table: "sensors",
                column: "spot_id",
                unique: true,
                filter: "spot_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_tariffs_facility_id",
                table: "tariffs",
                column: "facility_id");

            migrationBuilder.CreateIndex(
                name: "ix_users_email",
                table: "users",
                column: "email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_vehicles_license_plate",
                table: "vehicles",
                column: "license_plate",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_vehicles_user_id",
                table: "vehicles",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_zones_facility_id_name",
                table: "zones",
                columns: new[] { "facility_id", "name" },
                unique: true);

            // Hand-written (spec 001): EF Core cannot model EXCLUDE constraints. tstzrange is [from, to),
            // so consecutive versions and reservations may touch but never overlap. btree_gist provides
            // the gist equality on uuid.
            migrationBuilder.Sql(
                """
                ALTER TABLE tariffs ADD CONSTRAINT ex_tariffs_one_valid_version
                    EXCLUDE USING gist (facility_id WITH =, tstzrange(valid_from, valid_to) WITH &&);
                """);

            migrationBuilder.Sql(
                """
                ALTER TABLE reservations ADD CONSTRAINT ex_reservations_active_overlap
                    EXCLUDE USING gist (spot_id WITH =, tstzrange(starts_at, ends_at) WITH &&)
                    WHERE (status = 'Active');
                """);

            // Hand-written (spec 001): application role and permissions, after every table exists.
            migrationBuilder.Sql(Sql.MigrationSql.Read(Sql.MigrationSql.AppRole));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Hand-written (spec 001): the role is cluster-wide and may serve other databases, so it is kept;
            // only this database's grants go away (table grants disappear with the tables).
            migrationBuilder.Sql("REVOKE USAGE ON SCHEMA public FROM smartparking_app;");

            migrationBuilder.DropTable(
                name: "audit_entries");

            migrationBuilder.DropTable(
                name: "facility_opening_hours");

            migrationBuilder.DropTable(
                name: "outbox_messages");

            migrationBuilder.DropTable(
                name: "payments");

            migrationBuilder.DropTable(
                name: "sensors");

            migrationBuilder.DropTable(
                name: "parking_sessions");

            migrationBuilder.DropTable(
                name: "payment_methods");

            migrationBuilder.DropTable(
                name: "reservations");

            migrationBuilder.DropTable(
                name: "tariffs");

            migrationBuilder.DropTable(
                name: "parking_spots");

            migrationBuilder.DropTable(
                name: "vehicles");

            migrationBuilder.DropTable(
                name: "zones");

            migrationBuilder.DropTable(
                name: "users");

            migrationBuilder.DropTable(
                name: "parking_facilities");

            migrationBuilder.DropTable(
                name: "operators");
        }
    }
}
