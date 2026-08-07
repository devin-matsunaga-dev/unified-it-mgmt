using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Helpdesk.Data.Migrations
{
    /// <inheritdoc />
    public partial class WP19_CategoriesCustomFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "category_id",
                schema: "helpdesk",
                table: "tickets",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ticket_categories",
                schema: "helpdesk",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    parent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ticket_categories", x => x.id);
                    table.ForeignKey(
                        name: "fk_ticket_categories_ticket_categories_parent_id",
                        column: x => x.parent_id,
                        principalSchema: "helpdesk",
                        principalTable: "ticket_categories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ticket_custom_fields",
                schema: "helpdesk",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    category_id = table.Column<Guid>(type: "uuid", nullable: false),
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
                    table.PrimaryKey("pk_ticket_custom_fields", x => x.id);
                    table.ForeignKey(
                        name: "fk_ticket_custom_fields_ticket_categories_category_id",
                        column: x => x.category_id,
                        principalSchema: "helpdesk",
                        principalTable: "ticket_categories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ticket_custom_field_values",
                schema: "helpdesk",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: false),
                    field_id = table.Column<Guid>(type: "uuid", nullable: false),
                    value = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ticket_custom_field_values", x => x.id);
                    table.ForeignKey(
                        name: "fk_ticket_custom_field_values_ticket_custom_fields_field_id",
                        column: x => x.field_id,
                        principalSchema: "helpdesk",
                        principalTable: "ticket_custom_fields",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_ticket_custom_field_values_tickets_ticket_id",
                        column: x => x.ticket_id,
                        principalSchema: "helpdesk",
                        principalTable: "tickets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_tickets_category_id",
                schema: "helpdesk",
                table: "tickets",
                column: "category_id");

            migrationBuilder.CreateIndex(
                name: "ix_ticket_categories_is_active_sort_order",
                schema: "helpdesk",
                table: "ticket_categories",
                columns: new[] { "is_active", "sort_order" });

            migrationBuilder.CreateIndex(
                name: "ix_ticket_categories_parent_id_name",
                schema: "helpdesk",
                table: "ticket_categories",
                columns: new[] { "parent_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_ticket_custom_field_values_field_id",
                schema: "helpdesk",
                table: "ticket_custom_field_values",
                column: "field_id");

            migrationBuilder.CreateIndex(
                name: "ix_ticket_custom_field_values_ticket_id_field_id",
                schema: "helpdesk",
                table: "ticket_custom_field_values",
                columns: new[] { "ticket_id", "field_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_ticket_custom_fields_category_id_key",
                schema: "helpdesk",
                table: "ticket_custom_fields",
                columns: new[] { "category_id", "key" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_tickets_ticket_categories_category_id",
                schema: "helpdesk",
                table: "tickets",
                column: "category_id",
                principalSchema: "helpdesk",
                principalTable: "ticket_categories",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_tickets_ticket_categories_category_id",
                schema: "helpdesk",
                table: "tickets");

            migrationBuilder.DropTable(
                name: "ticket_custom_field_values",
                schema: "helpdesk");

            migrationBuilder.DropTable(
                name: "ticket_custom_fields",
                schema: "helpdesk");

            migrationBuilder.DropTable(
                name: "ticket_categories",
                schema: "helpdesk");

            migrationBuilder.DropIndex(
                name: "ix_tickets_category_id",
                schema: "helpdesk",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "category_id",
                schema: "helpdesk",
                table: "tickets");
        }
    }
}
