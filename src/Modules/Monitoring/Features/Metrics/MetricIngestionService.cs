using System.Text;

using Contracts.Events;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using Modules.Monitoring.Data;

using Npgsql;

using NpgsqlTypes;

namespace Modules.Monitoring.Features.Metrics;

public interface IMetricIngestionService
{
    /// <summary>Stores one telemetry batch. Returns how many metric rows it wrote.</summary>
    Task<int> IngestAsync(DeviceTelemetryReported telemetry, CancellationToken cancellationToken);
}

public sealed class MetricIngestionService(
    MonitoringDbContext dbContext,
    ILogger<MetricIngestionService> logger) : IMetricIngestionService
{
    /// <summary>
    /// Rows per INSERT. Eight parameters each, so this is comfortably under Postgres's 65535-parameter
    /// ceiling while still being one round trip for any realistic cycle.
    /// </summary>
    private const int InsertBatchSize = 500;

    public async Task<int> IngestAsync(DeviceTelemetryReported telemetry, CancellationToken cancellationToken)
    {
        var plan = TelemetryIngestionPlanner.Plan(telemetry);

        foreach (var rejection in plan.Rejected)
        {
            logger.LogWarning(
                "Telemetry from poller {PollerName} cycle {CycleNumber} rejected a sample. {Rejection}",
                telemetry.PollerName,
                telemetry.CycleNumber,
                rejection);
        }

        var written = 0;
        for (var offset = 0; offset < plan.Metrics.Count; offset += InsertBatchSize)
        {
            var batch = plan.Metrics.Skip(offset).Take(InsertBatchSize).ToList();
            written += await InsertMetricsAsync(batch, cancellationToken);
        }

        await UpsertInventoryAsync(plan.InventoryFacts, cancellationToken);
        return written;
    }

    /// <summary>
    /// <c>ON CONFLICT DO NOTHING</c> against the natural key, which is what makes a redelivered batch
    /// free. That is the backstop under the Platform dedupe helper rather than a replacement for it:
    /// the dedupe row and these rows live in two DbContexts and therefore two transactions, so a
    /// crash between them would otherwise replay the batch.
    /// </summary>
    private async Task<int> InsertMetricsAsync(
        IReadOnlyList<DeviceMetric> metrics,
        CancellationToken cancellationToken)
    {
        if (metrics.Count == 0)
        {
            return 0;
        }

        var sql = new StringBuilder(
            "INSERT INTO monitoring.device_metrics "
            + "(time, device_id, check_id, metric_name, value, unit, ci_id, poller_name) VALUES ");
        var parameters = new object[metrics.Count * 8];
        for (var index = 0; index < metrics.Count; index++)
        {
            var start = index * 8;
            if (index > 0)
            {
                sql.Append(", ");
            }

            sql.Append(System.Globalization.CultureInfo.InvariantCulture,
                $"({{{start}}}, {{{start + 1}}}, {{{start + 2}}}, {{{start + 3}}}, {{{start + 4}}}, {{{start + 5}}}, {{{start + 6}}}, {{{start + 7}}})");

            var metric = metrics[index];
            parameters[start] = Parameter(NpgsqlDbType.TimestampTz, metric.Time);
            parameters[start + 1] = Parameter(NpgsqlDbType.Uuid, metric.DeviceId);
            parameters[start + 2] = Parameter(NpgsqlDbType.Uuid, metric.CheckId);
            parameters[start + 3] = Parameter(NpgsqlDbType.Text, metric.MetricName);
            parameters[start + 4] = Parameter(NpgsqlDbType.Double, metric.Value);
            parameters[start + 5] = Parameter(NpgsqlDbType.Text, metric.Unit);
            parameters[start + 6] = Parameter(NpgsqlDbType.Uuid, metric.CiId);
            parameters[start + 7] = Parameter(NpgsqlDbType.Text, metric.PollerName);
        }

        sql.Append(" ON CONFLICT DO NOTHING;");

        // Raw SQL rather than EF's change tracker: this is an append-only insert of up to a few
        // thousand rows a cycle with a conflict clause EF cannot express. Every value is still a
        // parameter — the string carries placeholders only, never data.
        return await dbContext.Database.ExecuteSqlRawAsync(sql.ToString(), parameters, cancellationToken);
    }

    /// <summary>
    /// Explicitly typed, because a nullable column needs a parameter that knows what kind of null it
    /// is — EF's raw-SQL path has no store mapping for a bare <see cref="DBNull"/>.
    /// </summary>
    private static NpgsqlParameter Parameter(NpgsqlDbType type, object? value) =>
        new() { NpgsqlDbType = type, Value = value ?? DBNull.Value };

    /// <summary>
    /// Inventory is current state, so this overwrites — but only forwards, so a redelivered batch
    /// cannot restore a device's old sysDescr over the one it reports now.
    /// </summary>
    private async Task UpsertInventoryAsync(
        IReadOnlyList<DeviceInventoryFact> facts,
        CancellationToken cancellationToken)
    {
        if (facts.Count == 0)
        {
            return;
        }

        // The metrics table has no foreign key, but this one does: a fact is about a device that
        // exists. A device deleted between the poll and its ingestion would otherwise fail the whole
        // batch over a name nobody will read again.
        var deviceIds = facts.Select(fact => fact.DeviceId).Distinct().ToList();
        var known = await dbContext.MonitoredDevices
            .Where(device => deviceIds.Contains(device.Id))
            .Select(device => device.Id)
            .ToListAsync(cancellationToken);
        if (known.Count == 0)
        {
            return;
        }

        var knownSet = known.ToHashSet();
        foreach (var fact in facts.Where(fact => knownSet.Contains(fact.DeviceId)))
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO monitoring.device_inventory_facts (device_id, name, value, observed_at)
                VALUES ({0}, {1}, {2}, {3})
                ON CONFLICT (device_id, name) DO UPDATE
                    SET value = EXCLUDED.value, observed_at = EXCLUDED.observed_at
                    WHERE EXCLUDED.observed_at > monitoring.device_inventory_facts.observed_at;
                """,
                [fact.DeviceId, fact.Name, fact.Value, fact.ObservedAt],
                cancellationToken);
        }
    }
}
