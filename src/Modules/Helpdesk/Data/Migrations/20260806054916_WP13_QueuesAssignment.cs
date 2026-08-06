using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Helpdesk.Data.Migrations
{
    /// <inheritdoc />
    public partial class WP13_QueuesAssignment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "assigned_technician_id",
                schema: "helpdesk",
                table: "tickets",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "queue_id",
                schema: "helpdesk",
                table: "tickets",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "teams",
                schema: "helpdesk",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_teams", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "team_members",
                schema: "helpdesk",
                columns: table => new
                {
                    team_id = table.Column<Guid>(type: "uuid", nullable: false),
                    technician_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    added_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_team_members", x => new { x.team_id, x.technician_id });
                    table.ForeignKey(
                        name: "fk_team_members_teams_team_id",
                        column: x => x.team_id,
                        principalSchema: "helpdesk",
                        principalTable: "teams",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ticket_queues",
                schema: "helpdesk",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    team_id = table.Column<Guid>(type: "uuid", nullable: false),
                    last_assigned_technician_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ticket_queues", x => x.id);
                    table.ForeignKey(
                        name: "fk_ticket_queues_teams_team_id",
                        column: x => x.team_id,
                        principalSchema: "helpdesk",
                        principalTable: "teams",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ticket_assignment_history",
                schema: "helpdesk",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: false),
                    queue_id = table.Column<Guid>(type: "uuid", nullable: false),
                    from_technician_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    to_technician_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    actor_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ticket_assignment_history", x => x.id);
                    table.ForeignKey(
                        name: "fk_ticket_assignment_history_ticketqueues_queue_id",
                        column: x => x.queue_id,
                        principalSchema: "helpdesk",
                        principalTable: "ticket_queues",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_ticket_assignment_history_tickets_ticket_id",
                        column: x => x.ticket_id,
                        principalSchema: "helpdesk",
                        principalTable: "tickets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_tickets_assigned_technician_id",
                schema: "helpdesk",
                table: "tickets",
                column: "assigned_technician_id");

            migrationBuilder.CreateIndex(
                name: "ix_tickets_queue_id",
                schema: "helpdesk",
                table: "tickets",
                column: "queue_id");

            migrationBuilder.CreateIndex(
                name: "ix_teams_name",
                schema: "helpdesk",
                table: "teams",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_ticket_assignment_history_queue_id",
                schema: "helpdesk",
                table: "ticket_assignment_history",
                column: "queue_id");

            migrationBuilder.CreateIndex(
                name: "ix_ticket_assignment_history_ticket_id_occurred_at",
                schema: "helpdesk",
                table: "ticket_assignment_history",
                columns: new[] { "ticket_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_ticket_queues_team_id_name",
                schema: "helpdesk",
                table: "ticket_queues",
                columns: new[] { "team_id", "name" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_tickets_ticketqueues_queue_id",
                schema: "helpdesk",
                table: "tickets",
                column: "queue_id",
                principalSchema: "helpdesk",
                principalTable: "ticket_queues",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_tickets_ticketqueues_queue_id",
                schema: "helpdesk",
                table: "tickets");

            migrationBuilder.DropTable(
                name: "team_members",
                schema: "helpdesk");

            migrationBuilder.DropTable(
                name: "ticket_assignment_history",
                schema: "helpdesk");

            migrationBuilder.DropTable(
                name: "ticket_queues",
                schema: "helpdesk");

            migrationBuilder.DropTable(
                name: "teams",
                schema: "helpdesk");

            migrationBuilder.DropIndex(
                name: "ix_tickets_assigned_technician_id",
                schema: "helpdesk",
                table: "tickets");

            migrationBuilder.DropIndex(
                name: "ix_tickets_queue_id",
                schema: "helpdesk",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "assigned_technician_id",
                schema: "helpdesk",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "queue_id",
                schema: "helpdesk",
                table: "tickets");
        }
    }
}
