using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Monitoring.Data.Migrations
{
    /// <inheritdoc />
    public partial class WP51_RootCauseSuppression : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "root_cause_alert_id",
                schema: "monitoring",
                table: "alerts",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_alerts_root_cause_alert_id",
                schema: "monitoring",
                table: "alerts",
                column: "root_cause_alert_id",
                filter: "root_cause_alert_id IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "fk_alerts_alerts_root_cause_alert_id",
                schema: "monitoring",
                table: "alerts",
                column: "root_cause_alert_id",
                principalSchema: "monitoring",
                principalTable: "alerts",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_alerts_alerts_root_cause_alert_id",
                schema: "monitoring",
                table: "alerts");

            migrationBuilder.DropIndex(
                name: "ix_alerts_root_cause_alert_id",
                schema: "monitoring",
                table: "alerts");

            migrationBuilder.DropColumn(
                name: "root_cause_alert_id",
                schema: "monitoring",
                table: "alerts");
        }
    }
}
