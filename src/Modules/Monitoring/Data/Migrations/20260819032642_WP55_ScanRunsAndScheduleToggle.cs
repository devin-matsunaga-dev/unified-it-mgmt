using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Monitoring.Data.Migrations
{
    /// <inheritdoc />
    public partial class WP55_ScanRunsAndScheduleToggle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // true, not the generated false: every profile that already exists was scanning on its
            // interval before this column did, and a migration must not switch the estate's scanning
            // off on the way past. The default is dropped immediately afterwards so the column matches
            // the model, which deliberately carries no database default — EF treats a bool with one as
            // value-generated, and would then ignore an explicit `false` on insert.
            migrationBuilder.AddColumn<bool>(
                name: "schedule_enabled",
                schema: "monitoring",
                table: "scan_profiles",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.Sql(
                "ALTER TABLE monitoring.scan_profiles ALTER COLUMN schedule_enabled DROP DEFAULT;");

            migrationBuilder.CreateTable(
                name: "discovery_settings",
                schema: "monitoring",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    scheduled_scanning_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    updated_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_discovery_settings", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "scan_runs",
                schema: "monitoring",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    scan_profile_id = table.Column<Guid>(type: "uuid", nullable: false),
                    scan_profile_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    discovery_group = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    requested_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    discovery_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    dispatched_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    deadline_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    addresses_probed = table.Column<int>(type: "integer", nullable: true),
                    devices_found = table.Column<int>(type: "integer", nullable: true),
                    error = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_scan_runs", x => x.id);
                    table.ForeignKey(
                        name: "fk_scan_runs_scan_profiles_scan_profile_id",
                        column: x => x.scan_profile_id,
                        principalSchema: "monitoring",
                        principalTable: "scan_profiles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_scan_runs_discovery_group_status",
                schema: "monitoring",
                table: "scan_runs",
                columns: new[] { "discovery_group", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_scan_runs_one_queued_per_profile",
                schema: "monitoring",
                table: "scan_runs",
                column: "scan_profile_id",
                unique: true,
                filter: "status = 'Queued'");

            migrationBuilder.CreateIndex(
                name: "ix_scan_runs_status_requested_at",
                schema: "monitoring",
                table: "scan_runs",
                columns: new[] { "status", "requested_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "discovery_settings",
                schema: "monitoring");

            migrationBuilder.DropTable(
                name: "scan_runs",
                schema: "monitoring");

            migrationBuilder.DropColumn(
                name: "schedule_enabled",
                schema: "monitoring",
                table: "scan_profiles");
        }
    }
}
