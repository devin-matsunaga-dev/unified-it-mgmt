using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Modules.Assets.Data.Migrations
{
    /// <inheritdoc />
    public partial class WP58_ChangeRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "change_requests",
                schema: "assets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    sequence_number = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    planned_start_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    planned_end_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    include_dependents = table.Column<bool>(type: "boolean", nullable: false),
                    requested_by_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    requested_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    decided_by_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    decided_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    decision_note = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_change_requests", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "change_request_cis",
                schema: "assets",
                columns: table => new
                {
                    change_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ci_id = table.Column<Guid>(type: "uuid", nullable: false),
                    is_dependent = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_change_request_cis", x => new { x.change_request_id, x.ci_id });
                    table.ForeignKey(
                        name: "fk_change_request_cis_change_requests_change_request_id",
                        column: x => x.change_request_id,
                        principalSchema: "assets",
                        principalTable: "change_requests",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_change_request_cis_cis_ci_id",
                        column: x => x.ci_id,
                        principalSchema: "assets",
                        principalTable: "cis",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_change_request_cis_ci_id",
                schema: "assets",
                table: "change_request_cis",
                column: "ci_id");

            migrationBuilder.CreateIndex(
                name: "ix_change_requests_planned_start_at_planned_end_at",
                schema: "assets",
                table: "change_requests",
                columns: new[] { "planned_start_at", "planned_end_at" });

            migrationBuilder.CreateIndex(
                name: "ix_change_requests_sequence_number",
                schema: "assets",
                table: "change_requests",
                column: "sequence_number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_change_requests_status_planned_start_at",
                schema: "assets",
                table: "change_requests",
                columns: new[] { "status", "planned_start_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "change_request_cis",
                schema: "assets");

            migrationBuilder.DropTable(
                name: "change_requests",
                schema: "assets");
        }
    }
}
