using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Assets.Data.Migrations
{
    /// <inheritdoc />
    public partial class WP23_CiRelationships : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ci_relationships",
                schema: "assets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_ci_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_ci_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ci_relationships", x => x.id);
                    table.ForeignKey(
                        name: "fk_ci_relationships_cis_source_ci_id",
                        column: x => x.source_ci_id,
                        principalSchema: "assets",
                        principalTable: "cis",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_ci_relationships_cis_target_ci_id",
                        column: x => x.target_ci_id,
                        principalSchema: "assets",
                        principalTable: "cis",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_ci_relationships_source_ci_id",
                schema: "assets",
                table: "ci_relationships",
                column: "source_ci_id");

            migrationBuilder.CreateIndex(
                name: "ix_ci_relationships_source_ci_id_target_ci_id_type",
                schema: "assets",
                table: "ci_relationships",
                columns: new[] { "source_ci_id", "target_ci_id", "type" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_ci_relationships_target_ci_id",
                schema: "assets",
                table: "ci_relationships",
                column: "target_ci_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ci_relationships",
                schema: "assets");
        }
    }
}
