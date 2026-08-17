using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Monitoring.Data.Migrations
{
    /// <inheritdoc />
    public partial class WP56_Runbooks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "runbooks",
                schema: "monitoring",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false),
                    timeout_seconds = table.Column<int>(type: "integer", nullable: false),
                    max_executions_per_window = table.Column<int>(type: "integer", nullable: false),
                    rate_limit_window_minutes = table.Column<int>(type: "integer", nullable: false),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    created_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_runbooks", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "runbook_executions",
                schema: "monitoring",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    runbook_id = table.Column<Guid>(type: "uuid", nullable: false),
                    runbook_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    runbook_version = table.Column<int>(type: "integer", nullable: false),
                    trigger_id = table.Column<Guid>(type: "uuid", nullable: true),
                    alert_id = table.Column<Guid>(type: "uuid", nullable: true),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ci_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rule_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    parameters_json = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    requested_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    poller_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    dispatched_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    deadline_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    exit_code = table.Column<int>(type: "integer", nullable: true),
                    output = table.Column<string>(type: "character varying(16000)", maxLength: 16000, nullable: true),
                    error = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_runbook_executions", x => x.id);
                    table.ForeignKey(
                        name: "fk_runbook_executions_runbooks_runbook_id",
                        column: x => x.runbook_id,
                        principalSchema: "monitoring",
                        principalTable: "runbooks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "runbook_triggers",
                schema: "monitoring",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    runbook_id = table.Column<Guid>(type: "uuid", nullable: false),
                    metric_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    minimum_severity = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: true),
                    parameters_json = table.Column<string>(type: "jsonb", nullable: false),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    created_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_runbook_triggers", x => x.id);
                    table.ForeignKey(
                        name: "fk_runbook_triggers_runbooks_runbook_id",
                        column: x => x.runbook_id,
                        principalSchema: "monitoring",
                        principalTable: "runbooks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_runbook_executions_device_id",
                schema: "monitoring",
                table: "runbook_executions",
                column: "device_id");

            migrationBuilder.CreateIndex(
                name: "ix_runbook_executions_runbook_id_alert_id",
                schema: "monitoring",
                table: "runbook_executions",
                columns: new[] { "runbook_id", "alert_id" },
                unique: true,
                filter: "alert_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_runbook_executions_status_requested_at",
                schema: "monitoring",
                table: "runbook_executions",
                columns: new[] { "status", "requested_at" });

            migrationBuilder.CreateIndex(
                name: "ix_runbook_triggers_metric_name_is_enabled",
                schema: "monitoring",
                table: "runbook_triggers",
                columns: new[] { "metric_name", "is_enabled" });

            migrationBuilder.CreateIndex(
                name: "ix_runbook_triggers_runbook_id",
                schema: "monitoring",
                table: "runbook_triggers",
                column: "runbook_id");

            migrationBuilder.CreateIndex(
                name: "ix_runbooks_key",
                schema: "monitoring",
                table: "runbooks",
                column: "key",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "runbook_executions",
                schema: "monitoring");

            migrationBuilder.DropTable(
                name: "runbook_triggers",
                schema: "monitoring");

            migrationBuilder.DropTable(
                name: "runbooks",
                schema: "monitoring");
        }
    }
}
