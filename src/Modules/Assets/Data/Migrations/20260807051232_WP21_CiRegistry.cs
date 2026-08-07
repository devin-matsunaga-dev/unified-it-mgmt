using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Assets.Data.Migrations
{
    /// <inheritdoc />
    public partial class WP21_CiRegistry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "assets");

            migrationBuilder.CreateTable(
                name: "ci_custom_field_definitions",
                schema: "assets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    ci_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    key = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    label = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    is_required = table.Column<bool>(type: "boolean", nullable: false),
                    options = table.Column<List<string>>(type: "text[]", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ci_custom_field_definitions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "cis",
                schema: "assets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    asset_tag = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    serial_number = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ci_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    manufacturer = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    model = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    purpose = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    service_tier = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    management_ip = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: true),
                    network_vendor = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    port_count = table.Column<int>(type: "integer", nullable: true),
                    server_hostname = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: true),
                    operating_system = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    cpu_cores = table.Column<int>(type: "integer", nullable: true),
                    server_ram_gb = table.Column<int>(type: "integer", nullable: true),
                    software_vendor = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    virtual_hostname = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: true),
                    hypervisor = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    vcpu_cores = table.Column<int>(type: "integer", nullable: true),
                    virtual_ram_gb = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cis", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "ci_custom_field_values",
                schema: "assets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    ci_id = table.Column<Guid>(type: "uuid", nullable: false),
                    field_id = table.Column<Guid>(type: "uuid", nullable: false),
                    value = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ci_custom_field_values", x => x.id);
                    table.ForeignKey(
                        name: "fk_ci_custom_field_values_ci_custom_field_definitions_field_id",
                        column: x => x.field_id,
                        principalSchema: "assets",
                        principalTable: "ci_custom_field_definitions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_ci_custom_field_values_cis_ci_id",
                        column: x => x.ci_id,
                        principalSchema: "assets",
                        principalTable: "cis",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_ci_custom_field_definitions_ci_type_key",
                schema: "assets",
                table: "ci_custom_field_definitions",
                columns: new[] { "ci_type", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_ci_custom_field_values_ci_id_field_id",
                schema: "assets",
                table: "ci_custom_field_values",
                columns: new[] { "ci_id", "field_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_ci_custom_field_values_field_id",
                schema: "assets",
                table: "ci_custom_field_values",
                column: "field_id");

            migrationBuilder.CreateIndex(
                name: "ix_cis_asset_tag",
                schema: "assets",
                table: "cis",
                column: "asset_tag",
                unique: true,
                filter: "asset_tag IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_cis_ci_type_name",
                schema: "assets",
                table: "cis",
                columns: new[] { "ci_type", "name" });

            migrationBuilder.CreateIndex(
                name: "ix_cis_is_active",
                schema: "assets",
                table: "cis",
                column: "is_active");

            migrationBuilder.CreateIndex(
                name: "ix_cis_serial_number",
                schema: "assets",
                table: "cis",
                column: "serial_number",
                unique: true,
                filter: "serial_number IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ci_custom_field_values",
                schema: "assets");

            migrationBuilder.DropTable(
                name: "ci_custom_field_definitions",
                schema: "assets");

            migrationBuilder.DropTable(
                name: "cis",
                schema: "assets");
        }
    }
}
