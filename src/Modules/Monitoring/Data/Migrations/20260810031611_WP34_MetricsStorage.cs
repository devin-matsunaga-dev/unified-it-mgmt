using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Monitoring.Data.Migrations
{
    /// <inheritdoc />
    public partial class WP34_MetricsStorage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "device_inventory_facts",
                schema: "monitoring",
                columns: table => new
                {
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    value = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    observed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_device_inventory_facts", x => new { x.device_id, x.name });
                    table.ForeignKey(
                        name: "fk_device_inventory_facts_monitored_devices_device_id",
                        column: x => x.device_id,
                        principalSchema: "monitoring",
                        principalTable: "monitored_devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "device_metrics",
                schema: "monitoring",
                columns: table => new
                {
                    time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    check_id = table.Column<Guid>(type: "uuid", nullable: false),
                    metric_name = table.Column<string>(type: "text", nullable: false),
                    value = table.Column<double>(type: "double precision", nullable: false),
                    unit = table.Column<string>(type: "text", nullable: true),
                    ci_id = table.Column<Guid>(type: "uuid", nullable: false),
                    poller_name = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_device_metrics", x => new { x.time, x.device_id, x.check_id, x.metric_name });
                });

            migrationBuilder.CreateIndex(
                name: "ix_device_metrics_device_id_metric_name_time",
                schema: "monitoring",
                table: "device_metrics",
                columns: new[] { "device_id", "metric_name", "time" },
                descending: new[] { false, false, true });

            // ---- TimescaleDB ----
            // Everything below is the reason this table exists at all, and none of it has an EF
            // expression. The extension ships preloaded in the timescale/timescaledb-ha image the
            // AppHost and the test fixture both run, so this only ever creates the catalog entry.
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS timescaledb;");

            // One-day chunks: the raw retention below is thirty days, and retention drops whole
            // chunks, so the chunk interval is also the granularity at which old data actually
            // leaves. A seven-day default would keep readings up to a week past their thirty days.
            migrationBuilder.Sql(
                "SELECT create_hypertable('monitoring.device_metrics', by_range('time', INTERVAL '1 day'));");

            // The 5-minute rollup the WP asks for. WITH NO DATA because the table is empty here and
            // a refresh at migration time would be a no-op that still takes a lock.
            migrationBuilder.Sql("""
                CREATE MATERIALIZED VIEW monitoring.device_metrics_5m
                WITH (timescaledb.continuous) AS
                SELECT time_bucket(INTERVAL '5 minutes', time) AS bucket,
                       device_id,
                       check_id,
                       metric_name,
                       avg(value) AS avg_value,
                       min(value) AS min_value,
                       max(value) AS max_value,
                       count(*) AS sample_count
                FROM monitoring.device_metrics
                GROUP BY bucket, device_id, check_id, metric_name
                WITH NO DATA;
                """);

            // Real-time aggregation: a read of the rollup unions the materialised buckets with a live
            // aggregation over the raw table for the region the refresh has not reached yet. Without
            // it the newest five to ten minutes of every long-range chart are simply missing, which
            // on a monitoring dashboard is the part somebody is watching. Timescale defaults this off
            // from 2.13 onwards, so it has to be said explicitly.
            migrationBuilder.Sql(
                "ALTER MATERIALIZED VIEW monitoring.device_metrics_5m SET (timescaledb.materialized_only = false);");

            // Refreshed every five minutes over the last day. The end offset leaves the newest
            // bucket alone until it is closed, so a chart never reads a five-minute average of the
            // one reading that has arrived so far.
            migrationBuilder.Sql("""
                SELECT add_continuous_aggregate_policy('monitoring.device_metrics_5m',
                    start_offset => INTERVAL '1 day',
                    end_offset => INTERVAL '5 minutes',
                    schedule_interval => INTERVAL '5 minutes');
                """);

            // Raw 30 days, rolled up 1 year — the WP's numbers. These are background jobs owned by
            // Timescale's scheduler, not by anything in this codebase: nothing in application code
            // deletes a metric row.
            migrationBuilder.Sql(
                "SELECT add_retention_policy('monitoring.device_metrics', INTERVAL '30 days');");
            migrationBuilder.Sql(
                "SELECT add_retention_policy('monitoring.device_metrics_5m', INTERVAL '365 days');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The policies go with the objects they are attached to, but the continuous aggregate is
            // a view over the hypertable and has to be dropped before it.
            migrationBuilder.Sql("DROP MATERIALIZED VIEW IF EXISTS monitoring.device_metrics_5m;");

            migrationBuilder.DropTable(
                name: "device_inventory_facts",
                schema: "monitoring");

            migrationBuilder.DropTable(
                name: "device_metrics",
                schema: "monitoring");
        }
    }
}
