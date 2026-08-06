using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

using Platform.Data;

#nullable disable

namespace Platform.Data.Migrations;

[DbContext(typeof(PlatformDbContext))]
[Migration("202608060001_WP04_AuditLog")]
public partial class WP04_AuditLog : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(name: "platform");
        migrationBuilder.CreateTable(
            name: "audit_entries",
            schema: "platform",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                actor_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                action = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                entity_type = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                entity_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                before_json = table.Column<string>(type: "jsonb", nullable: true),
                after_json = table.Column<string>(type: "jsonb", nullable: true),
                occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                correlation_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_audit_entries", x => x.id);
                table.CheckConstraint("ck_audit_entries_actor_id_not_empty", "length(actor_id) > 0");
            });
        migrationBuilder.CreateIndex(
            name: "ix_audit_entries_entity_type_entity_id",
            schema: "platform",
            table: "audit_entries",
            columns: ["entity_type", "entity_id"]);
        migrationBuilder.CreateIndex(
            name: "ix_audit_entries_occurred_at",
            schema: "platform",
            table: "audit_entries",
            column: "occurred_at");
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "audit_entries", schema: "platform");
}