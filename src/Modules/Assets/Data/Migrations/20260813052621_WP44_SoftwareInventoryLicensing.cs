using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Assets.Data.Migrations
{
    /// <inheritdoc />
    public partial class WP44_SoftwareInventoryLicensing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "software_products",
                schema: "assets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    publisher = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    category = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_software_products", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "installed_software",
                schema: "assets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    ci_id = table.Column<Guid>(type: "uuid", nullable: false),
                    identity_key = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    raw_name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    raw_publisher = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    version = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    product_id = table.Column<Guid>(type: "uuid", nullable: true),
                    installed_on = table.Column<DateOnly>(type: "date", nullable: true),
                    source = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    sighting_count = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_installed_software", x => x.id);
                    table.ForeignKey(
                        name: "fk_installed_software_cis_ci_id",
                        column: x => x.ci_id,
                        principalSchema: "assets",
                        principalTable: "cis",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_installed_software_software_products_product_id",
                        column: x => x.product_id,
                        principalSchema: "assets",
                        principalTable: "software_products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "license_pools",
                schema: "assets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    reference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    entitlements = table.Column<int>(type: "integer", nullable: false),
                    purchase_date = table.Column<DateOnly>(type: "date", nullable: true),
                    expires_at = table.Column<DateOnly>(type: "date", nullable: true),
                    notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_license_pools", x => x.id);
                    table.ForeignKey(
                        name: "fk_license_pools_software_products_product_id",
                        column: x => x.product_id,
                        principalSchema: "assets",
                        principalTable: "software_products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "software_normalisation_rules",
                schema: "assets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    match_kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    pattern = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    priority = table.Column<int>(type: "integer", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_software_normalisation_rules", x => x.id);
                    table.ForeignKey(
                        name: "fk_software_normalisation_rules_software_products_product_id",
                        column: x => x.product_id,
                        principalSchema: "assets",
                        principalTable: "software_products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_installed_software_ci_id_identity_key",
                schema: "assets",
                table: "installed_software",
                columns: new[] { "ci_id", "identity_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_installed_software_product_id",
                schema: "assets",
                table: "installed_software",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "ix_installed_software_raw_name",
                schema: "assets",
                table: "installed_software",
                column: "raw_name");

            migrationBuilder.CreateIndex(
                name: "ix_license_pools_expires_at",
                schema: "assets",
                table: "license_pools",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_license_pools_product_id_name",
                schema: "assets",
                table: "license_pools",
                columns: new[] { "product_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_software_normalisation_rules_match_kind_pattern",
                schema: "assets",
                table: "software_normalisation_rules",
                columns: new[] { "match_kind", "pattern" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_software_normalisation_rules_product_id",
                schema: "assets",
                table: "software_normalisation_rules",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "ix_software_products_is_active",
                schema: "assets",
                table: "software_products",
                column: "is_active");

            migrationBuilder.CreateIndex(
                name: "ix_software_products_publisher_name",
                schema: "assets",
                table: "software_products",
                columns: new[] { "publisher", "name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "installed_software",
                schema: "assets");

            migrationBuilder.DropTable(
                name: "license_pools",
                schema: "assets");

            migrationBuilder.DropTable(
                name: "software_normalisation_rules",
                schema: "assets");

            migrationBuilder.DropTable(
                name: "software_products",
                schema: "assets");
        }
    }
}
