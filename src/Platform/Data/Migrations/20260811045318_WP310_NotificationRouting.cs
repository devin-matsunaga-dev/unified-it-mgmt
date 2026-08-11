using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Data.Migrations
{
    /// <inheritdoc />
    public partial class WP310_NotificationRouting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "notification_channels",
                schema: "platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    target = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notification_channels", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "user_notification_preferences",
                schema: "platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    email_address = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    email_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    minimum_severity = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    quiet_hours_start = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    quiet_hours_end = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    time_zone = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    digest_quiet_hours = table.Column<bool>(type: "boolean", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_notification_preferences", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "notification_deliveries",
                schema: "platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_kind = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    severity = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    subject = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    body = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    deep_link = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    dedupe_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    channel_id = table.Column<Guid>(type: "uuid", nullable: true),
                    channel_kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    target_redacted = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    user_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    rule_id = table.Column<Guid>(type: "uuid", nullable: true),
                    outcome = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    detail = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    release_after = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    digest_delivery_id = table.Column<Guid>(type: "uuid", nullable: true),
                    digest_of_count = table.Column<int>(type: "integer", nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notification_deliveries", x => x.id);
                    table.ForeignKey(
                        name: "fk_notification_deliveries_notification_channels_channel_id",
                        column: x => x.channel_id,
                        principalSchema: "platform",
                        principalTable: "notification_channels",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "notification_routing_rules",
                schema: "platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    channel_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_kind = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    minimum_severity = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    device_group = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    quiet_hours_start = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    quiet_hours_end = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    time_zone = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    digest_quiet_hours = table.Column<bool>(type: "boolean", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notification_routing_rules", x => x.id);
                    table.ForeignKey(
                        name: "fk_notification_routing_rules_notification_channels_channel_id",
                        column: x => x.channel_id,
                        principalSchema: "platform",
                        principalTable: "notification_channels",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_notification_channels_name",
                schema: "platform",
                table: "notification_channels",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_notification_deliveries_channel_id",
                schema: "platform",
                table: "notification_deliveries",
                column: "channel_id");

            migrationBuilder.CreateIndex(
                name: "ix_notification_deliveries_dedupe_key",
                schema: "platform",
                table: "notification_deliveries",
                column: "dedupe_key");

            migrationBuilder.CreateIndex(
                name: "ix_notification_deliveries_occurred_at",
                schema: "platform",
                table: "notification_deliveries",
                column: "occurred_at");

            migrationBuilder.CreateIndex(
                name: "ix_notification_deliveries_outcome_release_after",
                schema: "platform",
                table: "notification_deliveries",
                columns: new[] { "outcome", "release_after" });

            migrationBuilder.CreateIndex(
                name: "ix_notification_routing_rules_channel_id",
                schema: "platform",
                table: "notification_routing_rules",
                column: "channel_id");

            migrationBuilder.CreateIndex(
                name: "ix_notification_routing_rules_is_active",
                schema: "platform",
                table: "notification_routing_rules",
                column: "is_active");

            migrationBuilder.CreateIndex(
                name: "ix_notification_routing_rules_name",
                schema: "platform",
                table: "notification_routing_rules",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_user_notification_preferences_user_id",
                schema: "platform",
                table: "user_notification_preferences",
                column: "user_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "notification_deliveries",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "notification_routing_rules",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "user_notification_preferences",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "notification_channels",
                schema: "platform");
        }
    }
}
