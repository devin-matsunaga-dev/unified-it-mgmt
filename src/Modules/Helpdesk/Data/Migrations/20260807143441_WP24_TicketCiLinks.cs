using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Helpdesk.Data.Migrations
{
    /// <inheritdoc />
    public partial class WP24_TicketCiLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ticket_ci_links",
                schema: "helpdesk",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ci_id = table.Column<Guid>(type: "uuid", nullable: false),
                    linked_by_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    linked_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    linked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ticket_ci_links", x => x.id);
                    table.ForeignKey(
                        name: "fk_ticket_ci_links_tickets_ticket_id",
                        column: x => x.ticket_id,
                        principalSchema: "helpdesk",
                        principalTable: "tickets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_ticket_ci_links_ci_id",
                schema: "helpdesk",
                table: "ticket_ci_links",
                column: "ci_id");

            migrationBuilder.CreateIndex(
                name: "ix_ticket_ci_links_ticket_id_ci_id",
                schema: "helpdesk",
                table: "ticket_ci_links",
                columns: new[] { "ticket_id", "ci_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ticket_ci_links",
                schema: "helpdesk");
        }
    }
}
