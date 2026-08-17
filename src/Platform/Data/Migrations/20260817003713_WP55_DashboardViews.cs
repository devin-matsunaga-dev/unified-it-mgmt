using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Data.Migrations
{
    /// <summary>
    /// One dashboard layout per person becomes several named views (WP-5.5).
    /// <para>
    /// Hand-written as a <b>rename</b> rather than left as the drop-and-create EF scaffolds, because the
    /// earlier table is already applied — a fresh database runs <c>WP55_DashboardLayouts</c> immediately
    /// before this, and a dev database has been running it since the first cut of the feature. Dropping it
    /// would throw away whatever arrangement somebody had saved; renaming it makes that arrangement their
    /// first view, called "My dashboard", still active.
    /// </para>
    /// </summary>
    public partial class WP55_DashboardViews : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameTable(
                name: "dashboard_layouts",
                schema: "platform",
                newName: "dashboard_views",
                newSchema: "platform");

            // Postgres carries the primary key's constraint name through a table rename, so it is renamed
            // explicitly — otherwise the constraint keeps a name no model in this solution refers to.
            migrationBuilder.Sql(
                "ALTER TABLE platform.dashboard_views RENAME CONSTRAINT pk_dashboard_layouts TO pk_dashboard_views;");

            migrationBuilder.DropIndex(
                name: "ix_dashboard_layouts_owner_id",
                schema: "platform",
                table: "dashboard_views");

            // Defaults exist only to fill the rows that are already there; they are dropped immediately
            // afterwards so the column matches the model, which has no default.
            migrationBuilder.AddColumn<string>(
                name: "name",
                schema: "platform",
                table: "dashboard_views",
                type: "character varying(60)",
                maxLength: 60,
                nullable: false,
                defaultValue: "My dashboard");

            migrationBuilder.AddColumn<bool>(
                name: "is_active",
                schema: "platform",
                table: "dashboard_views",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.Sql(
                "ALTER TABLE platform.dashboard_views ALTER COLUMN name DROP DEFAULT;");
            migrationBuilder.Sql(
                "ALTER TABLE platform.dashboard_views ALTER COLUMN is_active DROP DEFAULT;");

            migrationBuilder.CreateIndex(
                name: "ix_dashboard_views_owner_id_is_active",
                schema: "platform",
                table: "dashboard_views",
                columns: new[] { "owner_id", "is_active" });

            migrationBuilder.CreateIndex(
                name: "ix_dashboard_views_owner_id_name",
                schema: "platform",
                table: "dashboard_views",
                columns: new[] { "owner_id", "name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_dashboard_views_owner_id_name",
                schema: "platform",
                table: "dashboard_views");

            migrationBuilder.DropIndex(
                name: "ix_dashboard_views_owner_id_is_active",
                schema: "platform",
                table: "dashboard_views");

            // Everything but the active view is discarded on the way back: the older shape holds one layout
            // per owner and there is no honest way to keep the rest.
            migrationBuilder.Sql(
                "DELETE FROM platform.dashboard_views WHERE is_active = false;");

            migrationBuilder.DropColumn(
                name: "is_active",
                schema: "platform",
                table: "dashboard_views");

            migrationBuilder.DropColumn(
                name: "name",
                schema: "platform",
                table: "dashboard_views");

            migrationBuilder.Sql(
                "ALTER TABLE platform.dashboard_views RENAME CONSTRAINT pk_dashboard_views TO pk_dashboard_layouts;");

            migrationBuilder.RenameTable(
                name: "dashboard_views",
                schema: "platform",
                newName: "dashboard_layouts",
                newSchema: "platform");

            migrationBuilder.CreateIndex(
                name: "ix_dashboard_layouts_owner_id",
                schema: "platform",
                table: "dashboard_layouts",
                column: "owner_id",
                unique: true);
        }
    }
}
