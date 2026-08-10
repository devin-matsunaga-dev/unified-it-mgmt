using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Monitoring.Data.Migrations
{
    /// <inheritdoc />
    public partial class WP35_AlertStateMachine : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "flap_threshold",
                schema: "monitoring",
                table: "check_definitions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "flap_window_seconds",
                schema: "monitoring",
                table: "check_definitions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "hysteresis_percent",
                schema: "monitoring",
                table: "check_definitions",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "recovery_cycles",
                schema: "monitoring",
                table: "check_definitions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "sustained_cycles",
                schema: "monitoring",
                table: "check_definitions",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "alerts",
                schema: "monitoring",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ci_id = table.Column<Guid>(type: "uuid", nullable: false),
                    check_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rule_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    metric_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    severity = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    summary = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    last_value = table.Column<double>(type: "double precision", nullable: true),
                    threshold = table.Column<double>(type: "double precision", nullable: true),
                    consecutive_breaches = table.Column<int>(type: "integer", nullable: false),
                    is_flapping = table.Column<bool>(type: "boolean", nullable: false),
                    suppression = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    raised_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_observed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    cleared_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    poller_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_alerts", x => x.id);
                    table.ForeignKey(
                        name: "fk_alerts_monitored_devices_device_id",
                        column: x => x.device_id,
                        principalSchema: "monitoring",
                        principalTable: "monitored_devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_alerts_device_id_rule_id",
                schema: "monitoring",
                table: "alerts",
                columns: new[] { "device_id", "rule_id" },
                unique: true,
                filter: "status = 'Open'");

            migrationBuilder.CreateIndex(
                name: "ix_alerts_status_severity_raised_at",
                schema: "monitoring",
                table: "alerts",
                columns: new[] { "status", "severity", "raised_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "alerts",
                schema: "monitoring");

            migrationBuilder.DropColumn(
                name: "flap_threshold",
                schema: "monitoring",
                table: "check_definitions");

            migrationBuilder.DropColumn(
                name: "flap_window_seconds",
                schema: "monitoring",
                table: "check_definitions");

            migrationBuilder.DropColumn(
                name: "hysteresis_percent",
                schema: "monitoring",
                table: "check_definitions");

            migrationBuilder.DropColumn(
                name: "recovery_cycles",
                schema: "monitoring",
                table: "check_definitions");

            migrationBuilder.DropColumn(
                name: "sustained_cycles",
                schema: "monitoring",
                table: "check_definitions");
        }
    }
}
