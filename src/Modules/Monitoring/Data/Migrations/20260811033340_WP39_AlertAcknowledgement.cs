using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Monitoring.Data.Migrations
{
    /// <inheritdoc />
    public partial class WP39_AlertAcknowledgement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "acknowledged_at",
                schema: "monitoring",
                table: "alerts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "acknowledged_by",
                schema: "monitoring",
                table: "alerts",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "acknowledged_by_name",
                schema: "monitoring",
                table: "alerts",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "acknowledged_at",
                schema: "monitoring",
                table: "alerts");

            migrationBuilder.DropColumn(
                name: "acknowledged_by",
                schema: "monitoring",
                table: "alerts");

            migrationBuilder.DropColumn(
                name: "acknowledged_by_name",
                schema: "monitoring",
                table: "alerts");
        }
    }
}
