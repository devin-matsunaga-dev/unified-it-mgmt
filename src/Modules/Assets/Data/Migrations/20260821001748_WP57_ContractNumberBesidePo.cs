using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Assets.Data.Migrations
{
    /// <inheritdoc />
    public partial class WP57_ContractNumberBesidePo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "contract_number",
                schema: "assets",
                table: "contracts",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "contract_number",
                schema: "assets",
                table: "contracts");
        }
    }
}
