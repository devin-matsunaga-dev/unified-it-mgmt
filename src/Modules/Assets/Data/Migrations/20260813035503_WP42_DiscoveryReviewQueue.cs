using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Assets.Data.Migrations
{
    /// <inheritdoc />
    public partial class WP42_DiscoveryReviewQueue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ci_discovery_facts",
                schema: "assets",
                columns: table => new
                {
                    ci_id = table.Column<Guid>(type: "uuid", nullable: false),
                    address = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: false),
                    hostname = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    responded_to_ping = table.Column<bool>(type: "boolean", nullable: false),
                    open_ports_json = table.Column<string>(type: "jsonb", nullable: false),
                    sys_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    sys_description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    sys_object_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    sys_location = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    sys_contact = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    uptime_seconds = table.Column<double>(type: "double precision", nullable: true),
                    neighbours_json = table.Column<string>(type: "jsonb", nullable: false),
                    discovery_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    scan_profile_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    last_scan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    sighting_count = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ci_discovery_facts", x => x.ci_id);
                    table.ForeignKey(
                        name: "fk_ci_discovery_facts_cis_ci_id",
                        column: x => x.ci_id,
                        principalSchema: "assets",
                        principalTable: "cis",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "discovered_devices",
                schema: "assets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    identity_key = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    address = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: false),
                    hostname = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    responded_to_ping = table.Column<bool>(type: "boolean", nullable: false),
                    open_ports_json = table.Column<string>(type: "jsonb", nullable: false),
                    sys_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    sys_description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    sys_object_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    sys_location = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    sys_contact = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    uptime_seconds = table.Column<double>(type: "double precision", nullable: true),
                    neighbours_json = table.Column<string>(type: "jsonb", nullable: false),
                    discovery_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    scan_profile_id = table.Column<Guid>(type: "uuid", nullable: false),
                    scan_profile_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    last_scan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ci_id = table.Column<Guid>(type: "uuid", nullable: true),
                    match_rule = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    contender_ci_ids_json = table.Column<string>(type: "jsonb", nullable: false),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    sighting_count = table.Column<int>(type: "integer", nullable: false),
                    reviewed_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    reviewed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    review_note = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_discovered_devices", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_discovered_devices_address",
                schema: "assets",
                table: "discovered_devices",
                column: "address");

            migrationBuilder.CreateIndex(
                name: "ix_discovered_devices_ci_id",
                schema: "assets",
                table: "discovered_devices",
                column: "ci_id");

            migrationBuilder.CreateIndex(
                name: "ix_discovered_devices_hostname",
                schema: "assets",
                table: "discovered_devices",
                column: "hostname");

            migrationBuilder.CreateIndex(
                name: "ix_discovered_devices_identity_key",
                schema: "assets",
                table: "discovered_devices",
                column: "identity_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_discovered_devices_status_last_seen_at",
                schema: "assets",
                table: "discovered_devices",
                columns: new[] { "status", "last_seen_at" });

            migrationBuilder.CreateIndex(
                name: "ix_discovered_devices_sys_name",
                schema: "assets",
                table: "discovered_devices",
                column: "sys_name");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ci_discovery_facts",
                schema: "assets");

            migrationBuilder.DropTable(
                name: "discovered_devices",
                schema: "assets");
        }
    }
}
