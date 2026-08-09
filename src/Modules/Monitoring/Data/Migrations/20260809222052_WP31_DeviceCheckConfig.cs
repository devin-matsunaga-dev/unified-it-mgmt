using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Monitoring.Data.Migrations
{
    /// <inheritdoc />
    public partial class WP31_DeviceCheckConfig : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "monitoring");

            migrationBuilder.CreateTable(
                name: "config_changes",
                schema: "monitoring",
                columns: table => new
                {
                    version = table.Column<long>(type: "bigint", nullable: false),
                    entity_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: true),
                    poller_group = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_config_changes", x => x.version);
                });

            migrationBuilder.CreateTable(
                name: "maintenance_windows",
                schema: "monitoring",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    starts_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ends_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    applies_to_all_devices = table.Column<bool>(type: "boolean", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_maintenance_windows", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "monitored_devices",
                schema: "monitoring",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    ci_id = table.Column<Guid>(type: "uuid", nullable: false),
                    address = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    poller_group = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    created_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_monitored_devices", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "pollers",
                schema: "monitoring",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    poller_group = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    agent_version = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    last_config_version = table.Column<long>(type: "bigint", nullable: false),
                    last_config_fetched_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    registered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_registered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_pollers", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "check_definitions",
                schema: "monitoring",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    interval_seconds = table.Column<int>(type: "integer", nullable: false),
                    timeout_seconds = table.Column<int>(type: "integer", nullable: false),
                    warning_threshold = table.Column<double>(type: "double precision", nullable: true),
                    critical_threshold = table.Column<double>(type: "double precision", nullable: true),
                    comparison = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    parameters_json = table.Column<string>(type: "jsonb", nullable: false),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    created_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_check_definitions", x => x.id);
                    table.ForeignKey(
                        name: "fk_check_definitions_monitored_devices_device_id",
                        column: x => x.device_id,
                        principalSchema: "monitoring",
                        principalTable: "monitored_devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "maintenance_window_devices",
                schema: "monitoring",
                columns: table => new
                {
                    maintenance_window_id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_maintenance_window_devices", x => new { x.maintenance_window_id, x.device_id });
                    table.ForeignKey(
                        name: "fk_maintenance_window_devices_maintenance_windows_maintenance_~",
                        column: x => x.maintenance_window_id,
                        principalSchema: "monitoring",
                        principalTable: "maintenance_windows",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_maintenance_window_devices_monitored_devices_device_id",
                        column: x => x.device_id,
                        principalSchema: "monitoring",
                        principalTable: "monitored_devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_check_definitions_device_id_name",
                schema: "monitoring",
                table: "check_definitions",
                columns: new[] { "device_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_config_changes_device_id",
                schema: "monitoring",
                table: "config_changes",
                column: "device_id");

            migrationBuilder.CreateIndex(
                name: "ix_config_changes_poller_group",
                schema: "monitoring",
                table: "config_changes",
                column: "poller_group");

            migrationBuilder.CreateIndex(
                name: "ix_maintenance_window_devices_device_id",
                schema: "monitoring",
                table: "maintenance_window_devices",
                column: "device_id");

            migrationBuilder.CreateIndex(
                name: "ix_maintenance_windows_is_active",
                schema: "monitoring",
                table: "maintenance_windows",
                column: "is_active");

            migrationBuilder.CreateIndex(
                name: "ix_maintenance_windows_starts_at_ends_at",
                schema: "monitoring",
                table: "maintenance_windows",
                columns: new[] { "starts_at", "ends_at" });

            migrationBuilder.CreateIndex(
                name: "ix_monitored_devices_ci_id",
                schema: "monitoring",
                table: "monitored_devices",
                column: "ci_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_monitored_devices_poller_group_is_enabled",
                schema: "monitoring",
                table: "monitored_devices",
                columns: new[] { "poller_group", "is_enabled" });

            migrationBuilder.CreateIndex(
                name: "ix_pollers_name",
                schema: "monitoring",
                table: "pollers",
                column: "name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "check_definitions",
                schema: "monitoring");

            migrationBuilder.DropTable(
                name: "config_changes",
                schema: "monitoring");

            migrationBuilder.DropTable(
                name: "maintenance_window_devices",
                schema: "monitoring");

            migrationBuilder.DropTable(
                name: "pollers",
                schema: "monitoring");

            migrationBuilder.DropTable(
                name: "maintenance_windows",
                schema: "monitoring");

            migrationBuilder.DropTable(
                name: "monitored_devices",
                schema: "monitoring");
        }
    }
}
