using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Modules.Helpdesk.Data.Migrations
{
    /// <inheritdoc />
    public partial class WP12_StatusWorkflow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "status_id",
                schema: "helpdesk",
                table: "tickets",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("10000000-0000-0000-0000-000000000001"));

            migrationBuilder.CreateTable(
                name: "ticket_statuses",
                schema: "helpdesk",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    display_order = table.Column<int>(type: "integer", nullable: false),
                    requires_resolution_note = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ticket_statuses", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "ticket_status_transitions",
                schema: "helpdesk",
                columns: table => new
                {
                    from_status_id = table.Column<Guid>(type: "uuid", nullable: false),
                    to_status_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ticket_status_transitions", x => new { x.from_status_id, x.to_status_id });
                    table.ForeignKey(
                        name: "fk_ticket_status_transitions_ticket_statuses_from_status_id",
                        column: x => x.from_status_id,
                        principalSchema: "helpdesk",
                        principalTable: "ticket_statuses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_ticket_status_transitions_ticket_statuses_to_status_id",
                        column: x => x.to_status_id,
                        principalSchema: "helpdesk",
                        principalTable: "ticket_statuses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ticket_transition_history",
                schema: "helpdesk",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: false),
                    from_status_id = table.Column<Guid>(type: "uuid", nullable: false),
                    to_status_id = table.Column<Guid>(type: "uuid", nullable: false),
                    resolution_note = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: true),
                    actor_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ticket_transition_history", x => x.id);
                    table.ForeignKey(
                        name: "fk_ticket_transition_history_ticket_statuses_from_status_id",
                        column: x => x.from_status_id,
                        principalSchema: "helpdesk",
                        principalTable: "ticket_statuses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_ticket_transition_history_ticket_statuses_to_status_id",
                        column: x => x.to_status_id,
                        principalSchema: "helpdesk",
                        principalTable: "ticket_statuses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_ticket_transition_history_tickets_ticket_id",
                        column: x => x.ticket_id,
                        principalSchema: "helpdesk",
                        principalTable: "tickets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                schema: "helpdesk",
                table: "ticket_statuses",
                columns: new[] { "id", "display_order", "name", "requires_resolution_note" },
                values: new object[,]
                {
                    { new Guid("10000000-0000-0000-0000-000000000001"), 1, "New", false },
                    { new Guid("10000000-0000-0000-0000-000000000002"), 2, "Triage", false },
                    { new Guid("10000000-0000-0000-0000-000000000003"), 3, "InProgress", false },
                    { new Guid("10000000-0000-0000-0000-000000000004"), 4, "Pending", false },
                    { new Guid("10000000-0000-0000-0000-000000000005"), 5, "Resolved", true },
                    { new Guid("10000000-0000-0000-0000-000000000006"), 6, "Closed", false }
                });

            migrationBuilder.InsertData(
                schema: "helpdesk",
                table: "ticket_status_transitions",
                columns: new[] { "from_status_id", "to_status_id" },
                values: new object[,]
                {
                    { new Guid("10000000-0000-0000-0000-000000000001"), new Guid("10000000-0000-0000-0000-000000000002") },
                    { new Guid("10000000-0000-0000-0000-000000000002"), new Guid("10000000-0000-0000-0000-000000000003") },
                    { new Guid("10000000-0000-0000-0000-000000000003"), new Guid("10000000-0000-0000-0000-000000000004") },
                    { new Guid("10000000-0000-0000-0000-000000000004"), new Guid("10000000-0000-0000-0000-000000000005") },
                    { new Guid("10000000-0000-0000-0000-000000000005"), new Guid("10000000-0000-0000-0000-000000000006") }
                });

            migrationBuilder.CreateIndex(
                name: "ix_tickets_status_id",
                schema: "helpdesk",
                table: "tickets",
                column: "status_id");

            migrationBuilder.CreateIndex(
                name: "ix_ticket_status_transitions_to_status_id",
                schema: "helpdesk",
                table: "ticket_status_transitions",
                column: "to_status_id");

            migrationBuilder.CreateIndex(
                name: "ix_ticket_statuses_name",
                schema: "helpdesk",
                table: "ticket_statuses",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_ticket_transition_history_from_status_id",
                schema: "helpdesk",
                table: "ticket_transition_history",
                column: "from_status_id");

            migrationBuilder.CreateIndex(
                name: "ix_ticket_transition_history_ticket_id_occurred_at",
                schema: "helpdesk",
                table: "ticket_transition_history",
                columns: new[] { "ticket_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_ticket_transition_history_to_status_id",
                schema: "helpdesk",
                table: "ticket_transition_history",
                column: "to_status_id");

            migrationBuilder.AddForeignKey(
                name: "fk_tickets_ticket_statuses_status_id",
                schema: "helpdesk",
                table: "tickets",
                column: "status_id",
                principalSchema: "helpdesk",
                principalTable: "ticket_statuses",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_tickets_ticket_statuses_status_id",
                schema: "helpdesk",
                table: "tickets");

            migrationBuilder.DropTable(
                name: "ticket_status_transitions",
                schema: "helpdesk");

            migrationBuilder.DropTable(
                name: "ticket_transition_history",
                schema: "helpdesk");

            migrationBuilder.DropTable(
                name: "ticket_statuses",
                schema: "helpdesk");

            migrationBuilder.DropIndex(
                name: "ix_tickets_status_id",
                schema: "helpdesk",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "status_id",
                schema: "helpdesk",
                table: "tickets");
        }
    }
}
