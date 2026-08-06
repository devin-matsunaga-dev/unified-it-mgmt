using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Data.Migrations
{
    /// <inheritdoc />
    public partial class WP05_MassTransit8Compatibility : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_outbox_states_bus_name_created",
                schema: "platform",
                table: "outbox_states");

            migrationBuilder.DropColumn(
                name: "bus_name",
                schema: "platform",
                table: "outbox_states");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_states_created",
                schema: "platform",
                table: "outbox_states",
                column: "created");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_enqueue_time",
                schema: "platform",
                table: "outbox_messages",
                column: "enqueue_time");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_expiration_time",
                schema: "platform",
                table: "outbox_messages",
                column: "expiration_time");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_outbox_states_created",
                schema: "platform",
                table: "outbox_states");

            migrationBuilder.DropIndex(
                name: "ix_outbox_messages_enqueue_time",
                schema: "platform",
                table: "outbox_messages");

            migrationBuilder.DropIndex(
                name: "ix_outbox_messages_expiration_time",
                schema: "platform",
                table: "outbox_messages");

            migrationBuilder.AddColumn<string>(
                name: "bus_name",
                schema: "platform",
                table: "outbox_states",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_outbox_states_bus_name_created",
                schema: "platform",
                table: "outbox_states",
                columns: new[] { "bus_name", "created" });
        }
    }
}
