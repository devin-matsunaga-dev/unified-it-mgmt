using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Monitoring.Data.Migrations
{
    /// <inheritdoc />
    public partial class WP55_ScanRunProgress : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "addresses_total",
                schema: "monitoring",
                table: "scan_runs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "last_responding_address",
                schema: "monitoring",
                table: "scan_runs",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "progress_at",
                schema: "monitoring",
                table: "scan_runs",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "addresses_total",
                schema: "monitoring",
                table: "scan_runs");

            migrationBuilder.DropColumn(
                name: "last_responding_address",
                schema: "monitoring",
                table: "scan_runs");

            migrationBuilder.DropColumn(
                name: "progress_at",
                schema: "monitoring",
                table: "scan_runs");
        }
    }
}
