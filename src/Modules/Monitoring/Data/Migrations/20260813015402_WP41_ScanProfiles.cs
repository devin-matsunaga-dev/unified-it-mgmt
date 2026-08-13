using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Monitoring.Data.Migrations
{
    /// <inheritdoc />
    public partial class WP41_ScanProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "scan_profiles",
                schema: "monitoring",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    discovery_group = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ranges_json = table.Column<string>(type: "jsonb", nullable: false),
                    ports_json = table.Column<string>(type: "jsonb", nullable: false),
                    interval_minutes = table.Column<int>(type: "integer", nullable: false),
                    timeout_seconds = table.Column<int>(type: "integer", nullable: false),
                    snmp_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    neighbour_discovery_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    created_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_scan_profiles", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_scan_profiles_discovery_group_is_enabled",
                schema: "monitoring",
                table: "scan_profiles",
                columns: new[] { "discovery_group", "is_enabled" });

            migrationBuilder.CreateIndex(
                name: "ix_scan_profiles_name",
                schema: "monitoring",
                table: "scan_profiles",
                column: "name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "scan_profiles",
                schema: "monitoring");
        }
    }
}
