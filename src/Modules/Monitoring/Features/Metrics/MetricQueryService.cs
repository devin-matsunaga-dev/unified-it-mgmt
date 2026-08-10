using Microsoft.EntityFrameworkCore;

using Modules.Monitoring.Data;

using Npgsql;

using NpgsqlTypes;

namespace Modules.Monitoring.Features.Metrics;

public interface IMetricQueryService
{
    /// <summary>The metrics this device has reported recently, for a chart's picker. Null if no such device.</summary>
    Task<IReadOnlyList<DeviceMetricSummary>?> ListMetricsAsync(Guid deviceId, CancellationToken cancellationToken);

    Task<MetricSeriesResult> GetSeriesAsync(MetricSeriesRequest request, CancellationToken cancellationToken);

    Task<DeviceInventoryResponse?> GetInventoryAsync(Guid deviceId, CancellationToken cancellationToken);
}

public enum MetricQueryOutcome
{
    Success,
    NotFound,
    Invalid,
}

/// <param name="ErrorKey">
/// Which field the refusal belongs to, so the 400 names the parameter the caller has to change.
/// Defaults to the range at the endpoint.
/// </param>
public sealed record MetricSeriesResult(
    MetricQueryOutcome Outcome,
    MetricSeriesResponse? Series = null,
    string? Error = null,
    string? ErrorKey = null);

public sealed class MetricQueryService(MonitoringDbContext dbContext) : IMetricQueryService
{
    /// <summary>How far back the metric picker looks. Beyond this a metric has stopped being reported.</summary>
    private static readonly TimeSpan MetricDiscoveryWindow = TimeSpan.FromDays(2);

    /// <summary>
    /// Where <see cref="MetricResolution.Auto"/> switches to the rollup. Six hours of a fifteen-second
    /// cycle is around 1,400 points, which is about as much as a chart the width of a screen can say
    /// anything with.
    /// </summary>
    private static readonly TimeSpan AutoRawLimit = TimeSpan.FromHours(6);

    /// <summary>
    /// The longest range raw resolution will answer, when a caller asks for it by name. Past this the
    /// request is refused rather than truncated or silently downsampled, because a chart cannot tell
    /// a short answer from a complete one — the same rule WP-2.3 applied to traversal depth.
    /// </summary>
    private static readonly TimeSpan MaxRawRange = TimeSpan.FromHours(24);

    /// <summary>Retention on the rollup, so a range that predates every stored point is refused early.</summary>
    private static readonly TimeSpan MaxRange = TimeSpan.FromDays(366);

    private const int FiveMinuteBucketSeconds = 300;

    public async Task<IReadOnlyList<DeviceMetricSummary>?> ListMetricsAsync(
        Guid deviceId,
        CancellationToken cancellationToken)
    {
        if (!await dbContext.MonitoredDevices.AnyAsync(device => device.Id == deviceId, cancellationToken))
        {
            return null;
        }

        var since = DateTimeOffset.UtcNow - MetricDiscoveryWindow;

        // DISTINCT ON is the newest row per series in one index scan; EF has no expression for it, so
        // the shipped entity is read through FromSql rather than a second result shape being invented.
        // Distinct on the metric *and the check*: every check reports check.success and
        // check.latency_ms, so on a four-check device those names are four series each, and collapsing
        // them to one would hide three of them from the picker.
        var latest = await dbContext.DeviceMetrics
            .FromSqlRaw(
                """
                SELECT DISTINCT ON (metric_name, check_id) *
                FROM monitoring.device_metrics
                WHERE device_id = {0} AND time >= {1}
                ORDER BY metric_name, check_id, time DESC
                """,
                deviceId,
                since)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        // The check's name, so a picker can say "CPU" rather than print a Guid. Read separately
        // because a metric row deliberately carries no foreign key to the check that produced it.
        var checkNames = await dbContext.CheckDefinitions
            .Where(check => check.DeviceId == deviceId)
            .Select(check => new { check.Id, check.Name })
            .ToDictionaryAsync(check => check.Id, check => check.Name, cancellationToken);

        return
        [
            .. latest
                .OrderBy(metric => metric.MetricName, StringComparer.Ordinal)
                .ThenBy(metric => checkNames.GetValueOrDefault(metric.CheckId), StringComparer.Ordinal)
                .Select(metric => new DeviceMetricSummary(
                    metric.MetricName,
                    metric.Unit,
                    metric.CheckId,
                    checkNames.GetValueOrDefault(metric.CheckId),
                    metric.Time,
                    metric.Value)),
        ];
    }

    public async Task<MetricSeriesResult> GetSeriesAsync(
        MetricSeriesRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.To <= request.From)
        {
            return new MetricSeriesResult(MetricQueryOutcome.Invalid, Error: "'to' must be after 'from'.");
        }

        var range = request.To - request.From;
        if (range > MaxRange)
        {
            return new MetricSeriesResult(
                MetricQueryOutcome.Invalid,
                Error: $"A range may cover at most {MaxRange.TotalDays:0} days; nothing older is retained.");
        }

        var resolution = request.Resolution switch
        {
            MetricResolution.Auto => range <= AutoRawLimit ? MetricResolution.Raw : MetricResolution.FiveMinute,
            var explicitly => explicitly,
        };

        if (resolution == MetricResolution.Raw && range > MaxRawRange)
        {
            return new MetricSeriesResult(
                MetricQueryOutcome.Invalid,
                Error: $"Raw resolution covers at most {MaxRawRange.TotalHours:0} hours; "
                    + "ask for 'FiveMinute' or 'Auto' over a longer range.");
        }

        if (!await dbContext.MonitoredDevices.AnyAsync(device => device.Id == request.DeviceId, cancellationToken))
        {
            return new MetricSeriesResult(MetricQueryOutcome.NotFound);
        }

        var metric = request.Metric.Trim();

        // Which checks reported this name in this window, asked of whichever relation is about to
        // answer — over a range older than raw retention the hypertable is empty and only the rollup
        // still knows. One check is the ordinary case and needs no help from the caller.
        var producers = await ProducingChecksAsync(request, metric, resolution, cancellationToken);
        var checkId = request.CheckId;
        if (checkId is null && producers.Count > 1)
        {
            return new MetricSeriesResult(
                MetricQueryOutcome.Invalid,
                Error: $"'{metric}' is reported by {producers.Count} checks on this device; "
                    + "name one with 'checkId'. "
                    + $"Candidates: {string.Join(", ", producers.Order())}.",
                ErrorKey: "checkId");
        }

        checkId ??= producers.Count == 1 ? producers[0] : null;

        // The check filter is `{4} IS NULL OR check_id = {4}` rather than two SQL strings, so the
        // filtered and unfiltered reads cannot drift apart.
        var checkParameter = new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = (object?)checkId ?? DBNull.Value };
        var buckets = resolution == MetricResolution.Raw
            ? await dbContext.MetricBuckets.FromSqlRaw(
                    """
                    SELECT time AS bucket, value AS avg_value, value AS min_value, value AS max_value,
                           1::bigint AS sample_count
                    FROM monitoring.device_metrics
                    WHERE device_id = {0} AND metric_name = {1} AND time >= {2} AND time < {3}
                      AND ({4} IS NULL OR check_id = {4})
                    ORDER BY time
                    """,
                    request.DeviceId, metric, request.From, request.To, checkParameter)
                .ToListAsync(cancellationToken)
            // Read straight from the continuous aggregate rather than re-aggregating the raw table:
            // that is the point of it, and real-time aggregation keeps the newest, not-yet-refreshed
            // bucket in the answer.
            : await dbContext.MetricBuckets.FromSqlRaw(
                    """
                    SELECT bucket, avg_value, min_value, max_value, sample_count
                    FROM monitoring.device_metrics_5m
                    WHERE device_id = {0} AND metric_name = {1} AND bucket >= {2} AND bucket < {3}
                      AND ({4} IS NULL OR check_id = {4})
                    ORDER BY bucket
                    """,
                    request.DeviceId, metric, request.From, request.To, checkParameter)
                .ToListAsync(cancellationToken);

        var unit = await dbContext.DeviceMetrics
            .Where(row => row.DeviceId == request.DeviceId
                && row.MetricName == metric
                && (checkId == null || row.CheckId == checkId))
            .OrderByDescending(row => row.Time)
            .Select(row => row.Unit)
            .FirstOrDefaultAsync(cancellationToken);

        var points = buckets
            .Select(bucket => new MetricPoint(
                bucket.Bucket,
                request.Aggregation switch
                {
                    MetricAggregation.Min => bucket.MinValue,
                    MetricAggregation.Max => bucket.MaxValue,
                    _ => bucket.AvgValue,
                },
                bucket.MinValue,
                bucket.MaxValue,
                bucket.SampleCount))
            .ToList();

        return new MetricSeriesResult(
            MetricQueryOutcome.Success,
            new MetricSeriesResponse(
                request.DeviceId,
                metric,
                checkId,
                unit,
                request.From,
                request.To,
                resolution,
                request.Aggregation,
                resolution == MetricResolution.Raw ? 0 : FiveMinuteBucketSeconds,
                points));
    }

    /// <summary>
    /// The checks that reported this metric name on this device inside the window, read from the same
    /// relation the series itself will come from. Ordinarily one; more than one whenever the name is
    /// a derived <c>check.*</c> fact, or two checks happen to measure the same thing.
    /// </summary>
    private async Task<List<Guid>> ProducingChecksAsync(
        MetricSeriesRequest request,
        string metric,
        MetricResolution resolution,
        CancellationToken cancellationToken)
    {
        var sql = resolution == MetricResolution.Raw
            ? """
              SELECT DISTINCT check_id AS "Value" FROM monitoring.device_metrics
              WHERE device_id = {0} AND metric_name = {1} AND time >= {2} AND time < {3}
              """
            : """
              SELECT DISTINCT check_id AS "Value" FROM monitoring.device_metrics_5m
              WHERE device_id = {0} AND metric_name = {1} AND bucket >= {2} AND bucket < {3}
              """;

        return await dbContext.Database
            .SqlQueryRaw<Guid>(sql, request.DeviceId, metric, request.From, request.To)
            .ToListAsync(cancellationToken);
    }

    public async Task<DeviceInventoryResponse?> GetInventoryAsync(
        Guid deviceId,
        CancellationToken cancellationToken)
    {
        if (!await dbContext.MonitoredDevices.AnyAsync(device => device.Id == deviceId, cancellationToken))
        {
            return null;
        }

        var facts = await dbContext.DeviceInventoryFacts
            .Where(fact => fact.DeviceId == deviceId)
            .OrderBy(fact => fact.Name)
            .Select(fact => new DeviceInventoryEntry(fact.Name, fact.Value, fact.ObservedAt))
            .ToListAsync(cancellationToken);

        return new DeviceInventoryResponse(deviceId, facts);
    }
}
