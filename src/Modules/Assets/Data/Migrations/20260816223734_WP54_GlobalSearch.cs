using Microsoft.EntityFrameworkCore.Migrations;
using NpgsqlTypes;

#nullable disable

namespace Modules.Assets.Data.Migrations
{
    /// <inheritdoc />
    public partial class WP54_GlobalSearch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<NpgsqlTsVector>(
                name: "search_vector",
                schema: "assets",
                table: "cis",
                type: "tsvector",
                nullable: false,
                computedColumnSql: "setweight(to_tsvector('english', coalesce(name, '') || ' ' || coalesce(asset_tag, '')\n    || ' ' || coalesce(serial_number, '')), 'A')\n|| setweight(to_tsvector('english', coalesce(server_hostname, '') || ' '\n    || coalesce(virtual_hostname, '') || ' ' || coalesce(management_ip, '')), 'B')\n|| setweight(to_tsvector('english', coalesce(description, '')), 'C')",
                stored: true);

            migrationBuilder.CreateIndex(
                name: "ix_cis_search_vector",
                schema: "assets",
                table: "cis",
                column: "search_vector")
                .Annotation("Npgsql:IndexMethod", "GIN");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_cis_search_vector",
                schema: "assets",
                table: "cis");

            migrationBuilder.DropColumn(
                name: "search_vector",
                schema: "assets",
                table: "cis");
        }
    }
}
