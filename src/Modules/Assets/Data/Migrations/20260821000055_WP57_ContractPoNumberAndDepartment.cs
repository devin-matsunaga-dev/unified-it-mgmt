using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Assets.Data.Migrations
{
    /// <inheritdoc />
    public partial class WP57_ContractPoNumberAndDepartment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "contract_number",
                schema: "assets",
                table: "contracts",
                newName: "po_number");

            migrationBuilder.RenameIndex(
                name: "ix_contracts_contract_number",
                schema: "assets",
                table: "contracts",
                newName: "ix_contracts_po_number");

            migrationBuilder.AddColumn<Guid>(
                name: "department_id",
                schema: "assets",
                table: "contracts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "department_name",
                schema: "assets",
                table: "contracts",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "department_id",
                schema: "assets",
                table: "contracts");

            migrationBuilder.DropColumn(
                name: "department_name",
                schema: "assets",
                table: "contracts");

            migrationBuilder.RenameColumn(
                name: "po_number",
                schema: "assets",
                table: "contracts",
                newName: "contract_number");

            migrationBuilder.RenameIndex(
                name: "ix_contracts_po_number",
                schema: "assets",
                table: "contracts",
                newName: "ix_contracts_contract_number");
        }
    }
}
