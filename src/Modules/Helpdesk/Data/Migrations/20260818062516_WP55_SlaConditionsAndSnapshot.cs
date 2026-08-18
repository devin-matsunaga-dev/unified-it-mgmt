using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Helpdesk.Data.Migrations
{
    /// <inheritdoc />
    public partial class WP55_SlaConditionsAndSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_sla_policies_priority_category_is_active",
                schema: "helpdesk",
                table: "sla_policies");

            // The old free-text category is dropped rather than migrated to category_id. It was never
            // matched against anything — StartAsync only ever selected policies where it was null —
            // so no policy in any database has a value here that was doing work.
            migrationBuilder.DropColumn(
                name: "category",
                schema: "helpdesk",
                table: "sla_policies");

            migrationBuilder.AddColumn<Guid>(
                name: "calendar_id",
                schema: "helpdesk",
                table: "ticket_slas",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<int>(
                name: "resolution_target_minutes",
                schema: "helpdesk",
                table: "ticket_slas",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "response_target_minutes",
                schema: "helpdesk",
                table: "ticket_slas",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "warning_percent",
                schema: "helpdesk",
                table: "ticket_slas",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AlterColumn<string>(
                name: "priority",
                schema: "helpdesk",
                table: "sla_policies",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(16)",
                oldMaxLength: 16);

            migrationBuilder.AddColumn<Guid>(
                name: "category_id",
                schema: "helpdesk",
                table: "sla_policies",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "sort_order",
                schema: "helpdesk",
                table: "sla_policies",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ticket_type",
                schema: "helpdesk",
                table: "sla_policies",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            // Backfill before the foreign key exists, or every running ticket would hold a zero
            // target and an all-zero calendar id the constraint would then reject. Copying from the
            // policy is exactly what the code did on every read until now, so no clock moves.
            migrationBuilder.Sql("""
                UPDATE helpdesk.ticket_slas AS s
                SET response_target_minutes = p.response_target_minutes,
                    resolution_target_minutes = p.resolution_target_minutes,
                    warning_percent = p.warning_percent,
                    calendar_id = p.calendar_id
                FROM helpdesk.sla_policies AS p
                WHERE p.id = s.policy_id;
                """);

            migrationBuilder.CreateIndex(
                name: "ix_ticket_slas_calendar_id",
                schema: "helpdesk",
                table: "ticket_slas",
                column: "calendar_id");

            migrationBuilder.CreateIndex(
                name: "ix_sla_policies_category_id",
                schema: "helpdesk",
                table: "sla_policies",
                column: "category_id");

            migrationBuilder.CreateIndex(
                name: "ix_sla_policies_is_active_sort_order",
                schema: "helpdesk",
                table: "sla_policies",
                columns: new[] { "is_active", "sort_order" });

            migrationBuilder.AddForeignKey(
                name: "fk_sla_policies_ticket_categories_category_id",
                schema: "helpdesk",
                table: "sla_policies",
                column: "category_id",
                principalSchema: "helpdesk",
                principalTable: "ticket_categories",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_ticket_slas_business_hours_calendars_calendar_id",
                schema: "helpdesk",
                table: "ticket_slas",
                column: "calendar_id",
                principalSchema: "helpdesk",
                principalTable: "business_hours_calendars",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_sla_policies_ticket_categories_category_id",
                schema: "helpdesk",
                table: "sla_policies");

            migrationBuilder.DropForeignKey(
                name: "fk_ticket_slas_business_hours_calendars_calendar_id",
                schema: "helpdesk",
                table: "ticket_slas");

            migrationBuilder.DropIndex(
                name: "ix_ticket_slas_calendar_id",
                schema: "helpdesk",
                table: "ticket_slas");

            migrationBuilder.DropIndex(
                name: "ix_sla_policies_category_id",
                schema: "helpdesk",
                table: "sla_policies");

            migrationBuilder.DropIndex(
                name: "ix_sla_policies_is_active_sort_order",
                schema: "helpdesk",
                table: "sla_policies");

            migrationBuilder.DropColumn(
                name: "calendar_id",
                schema: "helpdesk",
                table: "ticket_slas");

            migrationBuilder.DropColumn(
                name: "resolution_target_minutes",
                schema: "helpdesk",
                table: "ticket_slas");

            migrationBuilder.DropColumn(
                name: "response_target_minutes",
                schema: "helpdesk",
                table: "ticket_slas");

            migrationBuilder.DropColumn(
                name: "warning_percent",
                schema: "helpdesk",
                table: "ticket_slas");

            migrationBuilder.DropColumn(
                name: "category_id",
                schema: "helpdesk",
                table: "sla_policies");

            migrationBuilder.DropColumn(
                name: "sort_order",
                schema: "helpdesk",
                table: "sla_policies");

            migrationBuilder.DropColumn(
                name: "ticket_type",
                schema: "helpdesk",
                table: "sla_policies");

            migrationBuilder.AlterColumn<string>(
                name: "priority",
                schema: "helpdesk",
                table: "sla_policies",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(16)",
                oldMaxLength: 16,
                oldNullable: true);

            migrationBuilder.AddColumn<string>(
                name: "category",
                schema: "helpdesk",
                table: "sla_policies",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_sla_policies_priority_category_is_active",
                schema: "helpdesk",
                table: "sla_policies",
                columns: new[] { "priority", "category", "is_active" });
        }
    }
}
