using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Helpdesk.Data.Migrations;

[DbContext(typeof(HelpdeskDbContext))]
[Migration("20260807010200_WP17_RequesterEmail")]
public sealed class WP17_RequesterEmail : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "requester_email",
            schema: "helpdesk",
            table: "tickets",
            type: "character varying(320)",
            maxLength: 320,
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "requester_email",
            schema: "helpdesk",
            table: "tickets");
    }
}
