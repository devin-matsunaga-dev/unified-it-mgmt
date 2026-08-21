using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Assets.Data.Migrations
{
    /// <inheritdoc />
    public partial class WP57_ContractReminderRecipients : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string[]>(
                name: "recipients",
                schema: "assets",
                table: "contract_reminder_settings",
                type: "text[]",
                nullable: false,
                defaultValue: new string[0]);

            migrationBuilder.AlterColumn<string>(
                name: "recipient",
                schema: "assets",
                table: "contract_notifications",
                type: "character varying(1300)",
                maxLength: 1300,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(320)",
                oldMaxLength: 320);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "recipients",
                schema: "assets",
                table: "contract_reminder_settings");

            migrationBuilder.AlterColumn<string>(
                name: "recipient",
                schema: "assets",
                table: "contract_notifications",
                type: "character varying(320)",
                maxLength: 320,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(1300)",
                oldMaxLength: 1300);
        }
    }
}
