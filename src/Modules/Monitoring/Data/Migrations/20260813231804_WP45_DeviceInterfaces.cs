using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Monitoring.Data.Migrations
{
    /// <inheritdoc />
    public partial class WP45_DeviceInterfaces : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "device_interfaces",
                schema: "monitoring",
                columns: table => new
                {
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    if_index = table.Column<int>(type: "integer", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    alias = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    mac_address = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    interface_type = table.Column<int>(type: "integer", nullable: true),
                    admin_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    oper_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    speed_bits_per_second = table.Column<long>(type: "bigint", nullable: true),
                    bits_in_per_second = table.Column<double>(type: "double precision", nullable: true),
                    bits_out_per_second = table.Column<double>(type: "double precision", nullable: true),
                    utilisation_percent = table.Column<double>(type: "double precision", nullable: true),
                    errors_in_per_second = table.Column<double>(type: "double precision", nullable: true),
                    errors_out_per_second = table.Column<double>(type: "double precision", nullable: true),
                    discards_in_per_second = table.Column<double>(type: "double precision", nullable: true),
                    discards_out_per_second = table.Column<double>(type: "double precision", nullable: true),
                    check_id = table.Column<Guid>(type: "uuid", nullable: false),
                    observed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_device_interfaces", x => new { x.device_id, x.if_index });
                    table.ForeignKey(
                        name: "fk_device_interfaces_monitored_devices_device_id",
                        column: x => x.device_id,
                        principalSchema: "monitoring",
                        principalTable: "monitored_devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "device_interfaces",
                schema: "monitoring");
        }
    }
}
