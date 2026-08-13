using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Assets.Data.Migrations
{
    /// <inheritdoc />
    public partial class WP43_TopologyMaps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "topology_maps",
                schema: "assets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_topology_maps", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "topology_map_nodes",
                schema: "assets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    topology_map_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ci_id = table.Column<Guid>(type: "uuid", nullable: false),
                    x = table.Column<double>(type: "double precision", nullable: false),
                    y = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_topology_map_nodes", x => x.id);
                    table.ForeignKey(
                        name: "fk_topology_map_nodes_cis_ci_id",
                        column: x => x.ci_id,
                        principalSchema: "assets",
                        principalTable: "cis",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_topology_map_nodes_topology_maps_topology_map_id",
                        column: x => x.topology_map_id,
                        principalSchema: "assets",
                        principalTable: "topology_maps",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_topology_map_nodes_ci_id",
                schema: "assets",
                table: "topology_map_nodes",
                column: "ci_id");

            migrationBuilder.CreateIndex(
                name: "ix_topology_map_nodes_topology_map_id_ci_id",
                schema: "assets",
                table: "topology_map_nodes",
                columns: new[] { "topology_map_id", "ci_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_topology_maps_name",
                schema: "assets",
                table: "topology_maps",
                column: "name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "topology_map_nodes",
                schema: "assets");

            migrationBuilder.DropTable(
                name: "topology_maps",
                schema: "assets");
        }
    }
}
