using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Helpdesk.Data.Migrations;

[DbContext(typeof(HelpdeskDbContext))]
[Migration("20260807010100_WP17_RequesterDisplayName")]
public sealed class WP17_RequesterDisplayName : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "requester_display_name",
            schema: "helpdesk",
            table: "tickets",
            type: "character varying(200)",
            maxLength: 200,
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "requester_display_name",
            schema: "helpdesk",
            table: "tickets");
    }
}
