using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Monitoring.Data.Migrations
{
    /// <inheritdoc />
    public partial class WP58_MaintenanceWindowChangeOrigin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "change_request_id",
                schema: "monitoring",
                table: "maintenance_windows",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_maintenance_windows_change_request_id",
                schema: "monitoring",
                table: "maintenance_windows",
                column: "change_request_id",
                unique: true,
                filter: "change_request_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_maintenance_windows_change_request_id",
                schema: "monitoring",
                table: "maintenance_windows");

            migrationBuilder.DropColumn(
                name: "change_request_id",
                schema: "monitoring",
                table: "maintenance_windows");
        }
    }
}
