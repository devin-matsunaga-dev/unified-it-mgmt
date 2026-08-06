using System;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Data.Migrations
{
    /// <inheritdoc />
    public partial class WP08_DemoDataOrganization : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "departments",
                schema: "platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_departments", x => x.id);
                    table.CheckConstraint("ck_departments_code_not_empty", "length(code) > 0");
                });

            migrationBuilder.CreateTable(
                name: "sites",
                schema: "platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sites", x => x.id);
                    table.CheckConstraint("ck_sites_code_not_empty", "length(code) > 0");
                });

            migrationBuilder.CreateTable(
                name: "user_profiles",
                schema: "platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    username = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    role = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    site_id = table.Column<Guid>(type: "uuid", nullable: false),
                    department_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_profiles", x => x.id);
                    table.CheckConstraint("ck_user_profiles_role", "role IN ('Admin', 'Technician', 'Manager', 'EndUser')");
                    table.ForeignKey(
                        name: "fk_user_profiles_departments_department_id",
                        column: x => x.department_id,
                        principalSchema: "platform",
                        principalTable: "departments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_user_profiles_sites_site_id",
                        column: x => x.site_id,
                        principalSchema: "platform",
                        principalTable: "sites",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_departments_code",
                schema: "platform",
                table: "departments",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_sites_code",
                schema: "platform",
                table: "sites",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_user_profiles_department_id",
                schema: "platform",
                table: "user_profiles",
                column: "department_id");

            migrationBuilder.CreateIndex(
                name: "ix_user_profiles_email",
                schema: "platform",
                table: "user_profiles",
                column: "email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_user_profiles_site_id",
                schema: "platform",
                table: "user_profiles",
                column: "site_id");

            migrationBuilder.CreateIndex(
                name: "ix_user_profiles_username",
                schema: "platform",
                table: "user_profiles",
                column: "username",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "user_profiles",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "departments",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "sites",
                schema: "platform");
        }
    }
}