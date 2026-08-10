namespace Modules.Monitoring.Features.Metrics;

/// <summary>
/// Which stored resolution answers a series query. <see cref="Auto"/> is what a chart should send:
/// it picks raw while the range is short enough for raw to be readable and the five-minute rollup
/// once it is not, so the caller never has to know the retention numbers.
/// </summary>
public enum MetricResolution
{
    Auto,

    /// <summary>Every reading, straight from the hypertable. Kept 30 days.</summary>
    Raw,

    /// <summary>The five-minute continuous aggregate. Kept a year.</summary>
    FiveMinute,
}

/// <summary>
/// Which number a bucket contributes to the line. Raw resolution has one reading per bucket, so all
/// three agree there; over the rollup they are the average, floor and ceiling of the five minutes.
/// </summary>
public enum MetricAggregation
{
    Avg,
    Min,
    Max,
}

/// <param name="CheckId">
/// Which check's readings to plot. Optional, and only needed when more than one check on the device
/// reports this metric name — every check contributes <c>check.success</c> and <c>check.latency_ms</c>,
/// so on a device with an ICMP and three SNMP checks those names name four series, not one. Omitting
/// it where the metric is ambiguous is refused rather than answered with the four interleaved.
/// </param>
public sealed record MetricSeriesRequest(
    Guid DeviceId,
    string Metric,
    DateTimeOffset From,
    DateTimeOffset To,
    MetricResolution Resolution = MetricResolution.Auto,
    MetricAggregation Aggregation = MetricAggregation.Avg,
    Guid? CheckId = null);

/// <param name="Value">The aggregation the caller asked for — this is the line.</param>
/// <param name="MinValue">Floor of the bucket, for a band around the line. Equals <paramref name="Value"/> at raw resolution.</param>
/// <param name="SampleCount">Readings behind the bucket. 1 at raw resolution; 0 never appears — an empty bucket is an absent point.</param>
public sealed record MetricPoint(
    DateTimeOffset Timestamp,
    double Value,
    double MinValue,
    double MaxValue,
    long SampleCount);

/// <param name="Resolution">
/// What actually answered, never the <see cref="MetricResolution.Auto"/> that was asked for — a
/// chart has to be able to say "5-minute averages" rather than guess.
/// </param>
/// <param name="CheckId">Which check answered, once one has been resolved. Null only for an empty series.</param>
public sealed record MetricSeriesResponse(
    Guid DeviceId,
    string Metric,
    Guid? CheckId,
    string? Unit,
    DateTimeOffset From,
    DateTimeOffset To,
    MetricResolution Resolution,
    MetricAggregation Aggregation,
    int BucketSeconds,
    IReadOnlyList<MetricPoint> Points);

/// <summary>
/// One metric a device has actually reported, with its most recent reading. This is what populates a
/// chart's metric picker: the set is discovered from the data rather than declared anywhere, because
/// which metrics a check produces is the poller's business.
/// </summary>
/// <remarks>
/// One entry per metric <em>and check</em>, not per metric: the same name reported by two checks is
/// two series and a picker that showed one of them would silently hide the other.
/// </remarks>
public sealed record DeviceMetricSummary(
    string Metric,
    string? Unit,
    Guid CheckId,
    string? CheckName,
    DateTimeOffset LastObservedAt,
    double LastValue);

/// <summary>The latest text facts a device has reported — model, description, name.</summary>
public sealed record DeviceInventoryResponse(
    Guid DeviceId,
    IReadOnlyList<DeviceInventoryEntry> Facts);

public sealed record DeviceInventoryEntry(string Name, string Value, DateTimeOffset ObservedAt);
