using Microsoft.EntityFrameworkCore.Migrations;
using NpgsqlTypes;

#nullable disable

namespace Modules.Monitoring.Data.Migrations
{
    /// <inheritdoc />
    public partial class WP54_GlobalSearch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<NpgsqlTsVector>(
                name: "search_vector",
                schema: "monitoring",
                table: "monitored_devices",
                type: "tsvector",
                nullable: false)
                .Annotation("Npgsql:TsVectorConfig", "english")
                .Annotation("Npgsql:TsVectorProperties", new[] { "address", "poller_group", "notes" });

            migrationBuilder.AddColumn<NpgsqlTsVector>(
                name: "search_vector",
                schema: "monitoring",
                table: "alerts",
                type: "tsvector",
                nullable: false)
                .Annotation("Npgsql:TsVectorConfig", "english")
                .Annotation("Npgsql:TsVectorProperties", new[] { "summary", "rule_id", "metric_name" });

            migrationBuilder.CreateIndex(
                name: "ix_monitored_devices_search_vector",
                schema: "monitoring",
                table: "monitored_devices",
                column: "search_vector")
                .Annotation("Npgsql:IndexMethod", "GIN");

            migrationBuilder.CreateIndex(
                name: "ix_alerts_search_vector",
                schema: "monitoring",
                table: "alerts",
                column: "search_vector")
                .Annotation("Npgsql:IndexMethod", "GIN");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_monitored_devices_search_vector",
                schema: "monitoring",
                table: "monitored_devices");

            migrationBuilder.DropIndex(
                name: "ix_alerts_search_vector",
                schema: "monitoring",
                table: "alerts");

            migrationBuilder.DropColumn(
                name: "search_vector",
                schema: "monitoring",
                table: "monitored_devices");

            migrationBuilder.DropColumn(
                name: "search_vector",
                schema: "monitoring",
                table: "alerts");
        }
    }
}
