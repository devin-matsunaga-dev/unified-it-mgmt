using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Assets.Data.Migrations
{
    /// <inheritdoc />
    public partial class WP46_DriftReconciliation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "physical_audit_sessions",
                schema: "assets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    site_id = table.Column<Guid>(type: "uuid", nullable: true),
                    site_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    opened_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    opened_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    closed_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    note = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_physical_audit_sessions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "physical_audit_scans",
                schema: "assets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ci_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ci_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    code = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    scanned_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    scanned_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    note = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_physical_audit_scans", x => x.id);
                    table.ForeignKey(
                        name: "fk_physical_audit_scans_cis_ci_id",
                        column: x => x.ci_id,
                        principalSchema: "assets",
                        principalTable: "cis",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_physical_audit_scans_physical_audit_sessions_session_id",
                        column: x => x.session_id,
                        principalSchema: "assets",
                        principalTable: "physical_audit_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_physical_audit_scans_ci_id",
                schema: "assets",
                table: "physical_audit_scans",
                column: "ci_id");

            migrationBuilder.CreateIndex(
                name: "ix_physical_audit_scans_session_id_ci_id",
                schema: "assets",
                table: "physical_audit_scans",
                columns: new[] { "session_id", "ci_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_physical_audit_sessions_status_opened_at",
                schema: "assets",
                table: "physical_audit_sessions",
                columns: new[] { "status", "opened_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "physical_audit_scans",
                schema: "assets");

            migrationBuilder.DropTable(
                name: "physical_audit_sessions",
                schema: "assets");
        }
    }
}
