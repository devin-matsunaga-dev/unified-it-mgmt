using Microsoft.EntityFrameworkCore.Migrations;
using NpgsqlTypes;

#nullable disable

namespace Platform.Data.Migrations
{
    /// <inheritdoc />
    public partial class WP54_GlobalSearch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<NpgsqlTsVector>(
                name: "search_vector",
                schema: "platform",
                table: "user_profiles",
                type: "tsvector",
                nullable: false)
                .Annotation("Npgsql:TsVectorConfig", "english")
                .Annotation("Npgsql:TsVectorProperties", new[] { "display_name", "username", "email" });

            migrationBuilder.CreateIndex(
                name: "ix_user_profiles_search_vector",
                schema: "platform",
                table: "user_profiles",
                column: "search_vector")
                .Annotation("Npgsql:IndexMethod", "GIN");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_user_profiles_search_vector",
                schema: "platform",
                table: "user_profiles");

            migrationBuilder.DropColumn(
                name: "search_vector",
                schema: "platform",
                table: "user_profiles");
        }
    }
}
