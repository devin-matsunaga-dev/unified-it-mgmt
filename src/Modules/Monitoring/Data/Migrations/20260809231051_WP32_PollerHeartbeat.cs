using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Monitoring.Data.Migrations
{
    /// <inheritdoc />
    public partial class WP32_PollerHeartbeat : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "heartbeat_interval_seconds",
                schema: "monitoring",
                table: "pollers",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "heartbeat_missed_at",
                schema: "monitoring",
                table: "pollers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "last_cycle_number",
                schema: "monitoring",
                table: "pollers",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_heartbeat_at",
                schema: "monitoring",
                table: "pollers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "last_reported_device_count",
                schema: "monitoring",
                table: "pollers",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "ix_pollers_is_enabled_last_heartbeat_at",
                schema: "monitoring",
                table: "pollers",
                columns: new[] { "is_enabled", "last_heartbeat_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_pollers_is_enabled_last_heartbeat_at",
                schema: "monitoring",
                table: "pollers");

            migrationBuilder.DropColumn(
                name: "heartbeat_interval_seconds",
                schema: "monitoring",
                table: "pollers");

            migrationBuilder.DropColumn(
                name: "heartbeat_missed_at",
                schema: "monitoring",
                table: "pollers");

            migrationBuilder.DropColumn(
                name: "last_cycle_number",
                schema: "monitoring",
                table: "pollers");

            migrationBuilder.DropColumn(
                name: "last_heartbeat_at",
                schema: "monitoring",
                table: "pollers");

            migrationBuilder.DropColumn(
                name: "last_reported_device_count",
                schema: "monitoring",
                table: "pollers");
        }
    }
}
