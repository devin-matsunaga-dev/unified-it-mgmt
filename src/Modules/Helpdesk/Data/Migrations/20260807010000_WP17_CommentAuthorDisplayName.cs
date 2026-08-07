using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Helpdesk.Data.Migrations;

[DbContext(typeof(HelpdeskDbContext))]
[Migration("20260807010000_WP17_CommentAuthorDisplayName")]
public sealed class WP17_CommentAuthorDisplayName : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "author_display_name",
            schema: "helpdesk",
            table: "ticket_comments",
            type: "character varying(200)",
            maxLength: 200,
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "author_display_name",
            schema: "helpdesk",
            table: "ticket_comments");
    }
}
