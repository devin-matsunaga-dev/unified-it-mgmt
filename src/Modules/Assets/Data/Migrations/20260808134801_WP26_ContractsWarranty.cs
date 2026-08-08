using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Assets.Data.Migrations
{
    /// <inheritdoc />
    public partial class WP26_ContractsWarranty : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "contract_id",
                schema: "assets",
                table: "cis",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "purchase_date",
                schema: "assets",
                table: "cis",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "warranty_expires_at",
                schema: "assets",
                table: "cis",
                type: "date",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "contract_notifications",
                schema: "assets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    subject = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    subject_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subject_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    due_date = table.Column<DateOnly>(type: "date", nullable: false),
                    threshold_days = table.Column<int>(type: "integer", nullable: false),
                    recipient = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    message = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_contract_notifications", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "vendors",
                schema: "assets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    contact_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    contact_email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    contact_phone = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    website = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_vendors", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "contracts",
                schema: "assets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    vendor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    contract_number = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    start_date = table.Column<DateOnly>(type: "date", nullable: false),
                    end_date = table.Column<DateOnly>(type: "date", nullable: false),
                    auto_renews = table.Column<bool>(type: "boolean", nullable: false),
                    cost = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: true),
                    owner_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    owner_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    owner_email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_contracts", x => x.id);
                    table.ForeignKey(
                        name: "fk_contracts_vendors_vendor_id",
                        column: x => x.vendor_id,
                        principalSchema: "assets",
                        principalTable: "vendors",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_cis_contract_id",
                schema: "assets",
                table: "cis",
                column: "contract_id");

            migrationBuilder.CreateIndex(
                name: "ix_cis_warranty_expires_at",
                schema: "assets",
                table: "cis",
                column: "warranty_expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_contract_notifications_sent_at",
                schema: "assets",
                table: "contract_notifications",
                column: "sent_at");

            migrationBuilder.CreateIndex(
                name: "ix_contract_notifications_subject_subject_id_due_date_threshol~",
                schema: "assets",
                table: "contract_notifications",
                columns: new[] { "subject", "subject_id", "due_date", "threshold_days" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_contracts_contract_number",
                schema: "assets",
                table: "contracts",
                column: "contract_number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_contracts_end_date",
                schema: "assets",
                table: "contracts",
                column: "end_date");

            migrationBuilder.CreateIndex(
                name: "ix_contracts_vendor_id",
                schema: "assets",
                table: "contracts",
                column: "vendor_id");

            migrationBuilder.CreateIndex(
                name: "ix_vendors_is_active",
                schema: "assets",
                table: "vendors",
                column: "is_active");

            migrationBuilder.CreateIndex(
                name: "ix_vendors_name",
                schema: "assets",
                table: "vendors",
                column: "name",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_cis_contracts_contract_id",
                schema: "assets",
                table: "cis",
                column: "contract_id",
                principalSchema: "assets",
                principalTable: "contracts",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_cis_contracts_contract_id",
                schema: "assets",
                table: "cis");

            migrationBuilder.DropTable(
                name: "contract_notifications",
                schema: "assets");

            migrationBuilder.DropTable(
                name: "contracts",
                schema: "assets");

            migrationBuilder.DropTable(
                name: "vendors",
                schema: "assets");

            migrationBuilder.DropIndex(
                name: "ix_cis_contract_id",
                schema: "assets",
                table: "cis");

            migrationBuilder.DropIndex(
                name: "ix_cis_warranty_expires_at",
                schema: "assets",
                table: "cis");

            migrationBuilder.DropColumn(
                name: "contract_id",
                schema: "assets",
                table: "cis");

            migrationBuilder.DropColumn(
                name: "purchase_date",
                schema: "assets",
                table: "cis");

            migrationBuilder.DropColumn(
                name: "warranty_expires_at",
                schema: "assets",
                table: "cis");
        }
    }
}
