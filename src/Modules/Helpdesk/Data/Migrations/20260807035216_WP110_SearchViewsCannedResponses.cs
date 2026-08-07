using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NpgsqlTypes;

#nullable disable

namespace Modules.Helpdesk.Data.Migrations
{
    /// <inheritdoc />
    public partial class WP110_SearchViewsCannedResponses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<NpgsqlTsVector>(
                name: "search_vector",
                schema: "helpdesk",
                table: "tickets",
                type: "tsvector",
                nullable: false)
                .Annotation("Npgsql:TsVectorConfig", "english")
                .Annotation("Npgsql:TsVectorProperties", new[] { "title", "description" });

            migrationBuilder.AddColumn<NpgsqlTsVector>(
                name: "search_vector",
                schema: "helpdesk",
                table: "ticket_comments",
                type: "tsvector",
                nullable: false)
                .Annotation("Npgsql:TsVectorConfig", "english")
                .Annotation("Npgsql:TsVectorProperties", new[] { "body" });

            migrationBuilder.CreateTable(
                name: "canned_responses",
                schema: "helpdesk",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    body = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: false),
                    created_by_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_canned_responses", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "ticket_views",
                schema: "helpdesk",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    owner_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    owner_display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    is_shared = table.Column<bool>(type: "boolean", nullable: false),
                    filter_json = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ticket_views", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_tickets_search_vector",
                schema: "helpdesk",
                table: "tickets",
                column: "search_vector")
                .Annotation("Npgsql:IndexMethod", "GIN");

            migrationBuilder.CreateIndex(
                name: "ix_ticket_comments_search_vector",
                schema: "helpdesk",
                table: "ticket_comments",
                column: "search_vector")
                .Annotation("Npgsql:IndexMethod", "GIN");

            migrationBuilder.CreateIndex(
                name: "ix_canned_responses_name",
                schema: "helpdesk",
                table: "canned_responses",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_ticket_views_is_shared",
                schema: "helpdesk",
                table: "ticket_views",
                column: "is_shared");

            migrationBuilder.CreateIndex(
                name: "ix_ticket_views_owner_id_name",
                schema: "helpdesk",
                table: "ticket_views",
                columns: new[] { "owner_id", "name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "canned_responses",
                schema: "helpdesk");

            migrationBuilder.DropTable(
                name: "ticket_views",
                schema: "helpdesk");

            migrationBuilder.DropIndex(
                name: "ix_tickets_search_vector",
                schema: "helpdesk",
                table: "tickets");

            migrationBuilder.DropIndex(
                name: "ix_ticket_comments_search_vector",
                schema: "helpdesk",
                table: "ticket_comments");

            migrationBuilder.DropColumn(
                name: "search_vector",
                schema: "helpdesk",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "search_vector",
                schema: "helpdesk",
                table: "ticket_comments");
        }
    }
}
