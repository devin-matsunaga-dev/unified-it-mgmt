using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Helpdesk.Data.Migrations
{
    /// <inheritdoc />
    public partial class WP15_SlaEngine : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "business_hours_calendars",
                schema: "helpdesk",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    time_zone_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    working_days = table.Column<int>(type: "integer", nullable: false),
                    start_time = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    end_time = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_business_hours_calendars", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "sla_policies",
                schema: "helpdesk",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    priority = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    category = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    response_target_minutes = table.Column<int>(type: "integer", nullable: false),
                    resolution_target_minutes = table.Column<int>(type: "integer", nullable: false),
                    warning_percent = table.Column<int>(type: "integer", nullable: false),
                    calendar_id = table.Column<Guid>(type: "uuid", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sla_policies", x => x.id);
                    table.ForeignKey(
                        name: "fk_sla_policies_business_hours_calendars_calendar_id",
                        column: x => x.calendar_id,
                        principalSchema: "helpdesk",
                        principalTable: "business_hours_calendars",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ticket_slas",
                schema: "helpdesk",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: false),
                    policy_id = table.Column<Guid>(type: "uuid", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    active_since = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    accumulated_business_seconds = table.Column<double>(type: "double precision", nullable: false),
                    response_business_seconds = table.Column<double>(type: "double precision", nullable: true),
                    response_completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    resolution_completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    response_warning_raised = table.Column<bool>(type: "boolean", nullable: false),
                    resolution_warning_raised = table.Column<bool>(type: "boolean", nullable: false),
                    response_breached = table.Column<bool>(type: "boolean", nullable: false),
                    resolution_breached = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ticket_slas", x => x.id);
                    table.ForeignKey(
                        name: "fk_ticket_slas_sla_policies_policy_id",
                        column: x => x.policy_id,
                        principalSchema: "helpdesk",
                        principalTable: "sla_policies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_ticket_slas_tickets_ticket_id",
                        column: x => x.ticket_id,
                        principalSchema: "helpdesk",
                        principalTable: "tickets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                schema: "helpdesk",
                table: "ticket_status_transitions",
                columns: new[] { "from_status_id", "to_status_id" },
                values: new object[] { new Guid("10000000-0000-0000-0000-000000000004"), new Guid("10000000-0000-0000-0000-000000000003") });

            migrationBuilder.CreateIndex(
                name: "ix_business_hours_calendars_name",
                schema: "helpdesk",
                table: "business_hours_calendars",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_sla_policies_calendar_id",
                schema: "helpdesk",
                table: "sla_policies",
                column: "calendar_id");

            migrationBuilder.CreateIndex(
                name: "ix_sla_policies_priority_category_is_active",
                schema: "helpdesk",
                table: "sla_policies",
                columns: new[] { "priority", "category", "is_active" });

            migrationBuilder.CreateIndex(
                name: "ix_ticket_slas_policy_id",
                schema: "helpdesk",
                table: "ticket_slas",
                column: "policy_id");

            migrationBuilder.CreateIndex(
                name: "ix_ticket_slas_ticket_id",
                schema: "helpdesk",
                table: "ticket_slas",
                column: "ticket_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ticket_slas",
                schema: "helpdesk");

            migrationBuilder.DropTable(
                name: "sla_policies",
                schema: "helpdesk");

            migrationBuilder.DropTable(
                name: "business_hours_calendars",
                schema: "helpdesk");

            migrationBuilder.DeleteData(
                schema: "helpdesk",
                table: "ticket_status_transitions",
                keyColumns: new[] { "from_status_id", "to_status_id" },
                keyValues: new object[] { new Guid("10000000-0000-0000-0000-000000000004"), new Guid("10000000-0000-0000-0000-000000000003") });
        }
    }
}
