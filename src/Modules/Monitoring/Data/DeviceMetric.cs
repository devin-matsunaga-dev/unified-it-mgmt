namespace Modules.Monitoring.Data;

/// <summary>
/// One numeric measurement, in the <c>monitoring.device_metrics</c> hypertable. This is the only
/// table in the solution that is not a plain relation: it is partitioned by <see cref="Time"/> and
/// its rows are dropped wholesale by a retention policy rather than deleted by anything in code.
/// <para>
/// The key is (time, device, check, metric name) rather than a surrogate id, for two reasons. A
/// hypertable requires every unique index to carry the partitioning column, so a bare <c>id</c>
/// could not be unique anyway; and a natural key makes a redelivered telemetry batch re-insert
/// identical rows, which the ingestion's <c>ON CONFLICT DO NOTHING</c> then discards. That is the
/// backstop under the Platform dedupe helper, not a replacement for it — see
/// <c>DeviceTelemetryConsumer</c>.
/// </para>
/// </summary>
public sealed class DeviceMetric
{
    /// <summary>When the poller took the reading — its <c>ObservedAt</c>, never the ingestion time.</summary>
    public DateTimeOffset Time { get; set; }

    public Guid DeviceId { get; set; }

    public Guid CheckId { get; set; }

    /// <summary>
    /// What was measured (<c>cpu.utilisation</c>, <c>check.latency_ms</c>). The <c>check.</c> prefix
    /// is reserved for the facts ingestion derives from the check result itself; a poller-supplied
    /// sample using it is dropped rather than allowed to overwrite one.
    /// </summary>
    public required string MetricName { get; set; }

    public double Value { get; set; }

    /// <summary>Carried on every row rather than looked up, because a chart reads one table.</summary>
    public string? Unit { get; set; }

    /// <summary>
    /// The CI the device is, denormalised from the telemetry. A metric outlives nothing here — the
    /// device row can be deleted while its readings are still inside the retention window — so the
    /// correlation has to travel with the measurement.
    /// </summary>
    public Guid CiId { get; set; }

    public required string PollerName { get; set; }
}

/// <summary>
/// The latest text fact a device has reported — <c>sysDescr</c>, <c>sysName</c>. These arrive in the
/// same telemetry batch as the numbers but are deliberately not stored beside them: a hypertable
/// stores numbers, and "what model is this switch" is a current-state question, not a series. One
/// row per device and name, overwritten in place.
/// </summary>
public sealed class DeviceInventoryFact
{
    public Guid DeviceId { get; set; }

    public MonitoredDevice Device { get; set; } = null!;

    public required string Name { get; set; }

    public required string Value { get; set; }

    /// <summary>When the poller read it, not when this row was written.</summary>
    public DateTimeOffset ObservedAt { get; set; }
}
