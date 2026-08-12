using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Monitoring.Data.Migrations
{
    /// <inheritdoc />
    public partial class WP311_CheckCredential : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "credential_id",
                schema: "monitoring",
                table: "check_definitions",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_check_definitions_credential_id",
                schema: "monitoring",
                table: "check_definitions",
                column: "credential_id",
                filter: "credential_id is not null");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_check_definitions_credential_id",
                schema: "monitoring",
                table: "check_definitions");

            migrationBuilder.DropColumn(
                name: "credential_id",
                schema: "monitoring",
                table: "check_definitions");
        }
    }
}
