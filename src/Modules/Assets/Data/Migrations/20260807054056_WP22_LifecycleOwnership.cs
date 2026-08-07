using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Modules.Assets.Data.Migrations
{
    /// <inheritdoc />
    public partial class WP22_LifecycleOwnership : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "assigned_at",
                schema: "assets",
                table: "cis",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "department_id",
                schema: "assets",
                table: "cis",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "department_name",
                schema: "assets",
                table: "cis",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "lifecycle_state",
                schema: "assets",
                table: "cis",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                // CIs registered by WP-2.1 predate the lifecycle, and one that was never deployed is
                // in stock; the empty string EF scaffolds by default is not a valid state.
                defaultValue: "InStock");

            migrationBuilder.AddColumn<string>(
                name: "owner_name",
                schema: "assets",
                table: "cis",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "owner_user_id",
                schema: "assets",
                table: "cis",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "site_id",
                schema: "assets",
                table: "cis",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "site_name",
                schema: "assets",
                table: "cis",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ci_assignments",
                schema: "assets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    ci_id = table.Column<Guid>(type: "uuid", nullable: false),
                    action = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    from_owner_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    from_owner_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    to_owner_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    to_owner_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    department_id = table.Column<Guid>(type: "uuid", nullable: true),
                    department_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    site_id = table.Column<Guid>(type: "uuid", nullable: true),
                    site_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    actor_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ci_assignments", x => x.id);
                    table.ForeignKey(
                        name: "fk_ci_assignments_cis_ci_id",
                        column: x => x.ci_id,
                        principalSchema: "assets",
                        principalTable: "cis",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ci_lifecycle_history",
                schema: "assets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    ci_id = table.Column<Guid>(type: "uuid", nullable: false),
                    from_state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    to_state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    actor_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ci_lifecycle_history", x => x.id);
                    table.ForeignKey(
                        name: "fk_ci_lifecycle_history_cis_ci_id",
                        column: x => x.ci_id,
                        principalSchema: "assets",
                        principalTable: "cis",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ci_lifecycle_transitions",
                schema: "assets",
                columns: table => new
                {
                    from_state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    to_state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ci_lifecycle_transitions", x => new { x.from_state, x.to_state });
                });

            migrationBuilder.InsertData(
                schema: "assets",
                table: "ci_lifecycle_transitions",
                columns: new[] { "from_state", "to_state" },
                values: new object[,]
                {
                    { "Deployed", "InRepair" },
                    { "Deployed", "InStock" },
                    { "Deployed", "Retired" },
                    { "InRepair", "Deployed" },
                    { "InRepair", "InStock" },
                    { "InRepair", "Retired" },
                    { "InStock", "Deployed" },
                    { "InStock", "InRepair" },
                    { "InStock", "Retired" },
                    { "Ordered", "InStock" },
                    { "Retired", "Disposed" }
                });

            migrationBuilder.CreateIndex(
                name: "ix_cis_department_id",
                schema: "assets",
                table: "cis",
                column: "department_id");

            migrationBuilder.CreateIndex(
                name: "ix_cis_lifecycle_state",
                schema: "assets",
                table: "cis",
                column: "lifecycle_state");

            migrationBuilder.CreateIndex(
                name: "ix_cis_owner_user_id",
                schema: "assets",
                table: "cis",
                column: "owner_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_cis_site_id",
                schema: "assets",
                table: "cis",
                column: "site_id");

            migrationBuilder.CreateIndex(
                name: "ix_ci_assignments_ci_id_occurred_at",
                schema: "assets",
                table: "ci_assignments",
                columns: new[] { "ci_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_ci_lifecycle_history_ci_id_occurred_at",
                schema: "assets",
                table: "ci_lifecycle_history",
                columns: new[] { "ci_id", "occurred_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ci_assignments",
                schema: "assets");

            migrationBuilder.DropTable(
                name: "ci_lifecycle_history",
                schema: "assets");

            migrationBuilder.DropTable(
                name: "ci_lifecycle_transitions",
                schema: "assets");

            migrationBuilder.DropIndex(
                name: "ix_cis_department_id",
                schema: "assets",
                table: "cis");

            migrationBuilder.DropIndex(
                name: "ix_cis_lifecycle_state",
                schema: "assets",
                table: "cis");

            migrationBuilder.DropIndex(
                name: "ix_cis_owner_user_id",
                schema: "assets",
                table: "cis");

            migrationBuilder.DropIndex(
                name: "ix_cis_site_id",
                schema: "assets",
                table: "cis");

            migrationBuilder.DropColumn(
                name: "assigned_at",
                schema: "assets",
                table: "cis");

            migrationBuilder.DropColumn(
                name: "department_id",
                schema: "assets",
                table: "cis");

            migrationBuilder.DropColumn(
                name: "department_name",
                schema: "assets",
                table: "cis");

            migrationBuilder.DropColumn(
                name: "lifecycle_state",
                schema: "assets",
                table: "cis");

            migrationBuilder.DropColumn(
                name: "owner_name",
                schema: "assets",
                table: "cis");

            migrationBuilder.DropColumn(
                name: "owner_user_id",
                schema: "assets",
                table: "cis");

            migrationBuilder.DropColumn(
                name: "site_id",
                schema: "assets",
                table: "cis");

            migrationBuilder.DropColumn(
                name: "site_name",
                schema: "assets",
                table: "cis");
        }
    }
}
