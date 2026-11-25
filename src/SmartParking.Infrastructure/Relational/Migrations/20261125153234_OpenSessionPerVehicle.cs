using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartParking.Infrastructure.Relational.Migrations
{
    /// <inheritdoc />
    public partial class OpenSessionPerVehicle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ux_parking_sessions_vehicle_open",
                table: "parking_sessions",
                column: "vehicle_id",
                unique: true,
                filter: "status = 'Open'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_parking_sessions_vehicle_open",
                table: "parking_sessions");
        }
    }
}
