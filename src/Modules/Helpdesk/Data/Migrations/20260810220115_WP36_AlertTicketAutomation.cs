using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Helpdesk.Data.Migrations
{
    /// <inheritdoc />
    public partial class WP36_AlertTicketAutomation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "alert_tickets",
                schema: "helpdesk",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    dedupe_key = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ci_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rule_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    alert_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: true),
                    last_severity = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    occurrence_count = table.Column<int>(type: "integer", nullable: false),
                    suppressed_count = table.Column<int>(type: "integer", nullable: false),
                    ticket_count = table.Column<int>(type: "integer", nullable: false),
                    first_raised_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_raised_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_cleared_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ticket_created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    auto_resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_alert_tickets", x => x.id);
                    table.ForeignKey(
                        name: "fk_alert_tickets_tickets_ticket_id",
                        column: x => x.ticket_id,
                        principalSchema: "helpdesk",
                        principalTable: "tickets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "ix_alert_tickets_dedupe_key",
                schema: "helpdesk",
                table: "alert_tickets",
                column: "dedupe_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_alert_tickets_device_id",
                schema: "helpdesk",
                table: "alert_tickets",
                column: "device_id");

            migrationBuilder.CreateIndex(
                name: "ix_alert_tickets_ticket_created_at",
                schema: "helpdesk",
                table: "alert_tickets",
                column: "ticket_created_at");

            migrationBuilder.CreateIndex(
                name: "ix_alert_tickets_ticket_id",
                schema: "helpdesk",
                table: "alert_tickets",
                column: "ticket_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "alert_tickets",
                schema: "helpdesk");
        }
    }
}
