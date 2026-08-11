namespace Modules.Monitoring.Data;

/// <summary>
/// A CMDB CI that this platform polls. The device carries only the facts polling needs — the address
/// to reach it on and which poller owns it — and never a copy of the CI's own fields: name, type and
/// site are read live through <see cref="Platform.Integration.ICiDirectory"/>, following the WP-2.4
/// rule that a CI cannot leave the estate behind a link the way a person can leave a directory.
/// </summary>
public sealed class MonitoredDevice
{
    public Guid Id { get; set; }

    /// <summary>The CI this device is. One CI is monitored at most once, so this is unique.</summary>
    public Guid CiId { get; set; }

    /// <summary>Hostname or IP the pollers reach the device on. A monitoring fact, not a CMDB one.</summary>
    public required string Address { get; set; }

    /// <summary>Which poller polls it. Devices and pollers meet on this string and nothing else.</summary>
    public required string PollerGroup { get; set; }

    /// <summary>A disabled device stays configured but leaves every poller's config.</summary>
    public bool IsEnabled { get; set; } = true;

    public string? Notes { get; set; }

    public required string CreatedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public required string UpdatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public ICollection<CheckDefinition> Checks { get; set; } = [];
}

/// <summary>
/// What to measure on a device and when to worry. WP-3.1 stores and serves these; the poller acts on
/// them in WP-3.3/3.8 and the thresholds drive the alert state machine in WP-3.5.
/// </summary>
public sealed class CheckDefinition
{
    public Guid Id { get; set; }

    public Guid DeviceId { get; set; }

    public MonitoredDevice Device { get; set; } = null!;

    public CheckType Type { get; set; }

    public required string Name { get; set; }

    /// <summary>Seconds between runs. The poller's cycle length is its own business; this is per check.</summary>
    public int IntervalSeconds { get; set; }

    public int TimeoutSeconds { get; set; }

    public double? WarningThreshold { get; set; }

    public double? CriticalThreshold { get; set; }

    /// <summary>Which side of the threshold is bad — latency rises, free disk falls.</summary>
    public ThresholdComparison Comparison { get; set; }

    /// <summary>
    /// Per-type settings (SNMP OID, TCP port, HTTP URL). Held as a jsonb string and mapped to a
    /// dictionary in the service, following the WP-1.10 saved-view precedent, because the keys a
    /// check type needs are the check type's business rather than the schema's.
    /// </summary>
    public required string ParametersJson { get; set; }

    /// <summary>
    /// How many consecutive bad readings this check needs before its rules get worse. Null means the
    /// platform default in <c>Monitoring:Alerting</c> — the same rule for all five of these columns,
    /// so a check that nobody has tuned carries no copy of the defaults to drift from them. WP-3.5.
    /// </summary>
    public int? SustainedCycles { get; set; }

    /// <summary>Consecutive good readings before an alert on this check clears. Null: platform default.</summary>
    public int? RecoveryCycles { get; set; }

    /// <summary>How far back past a threshold a value must come, as a percentage of it. Null: platform default.</summary>
    public double? HysteresisPercent { get; set; }

    /// <summary>State changes inside the flap window that mean this check is flapping. Null: platform default.</summary>
    public int? FlapThreshold { get; set; }

    /// <summary>The period flap changes are counted over. Null: platform default.</summary>
    public int? FlapWindowSeconds { get; set; }

    public bool IsEnabled { get; set; } = true;

    public required string CreatedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public required string UpdatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public enum CheckType
{
    /// <summary>Reachability. WP-3.3.</summary>
    Icmp,

    /// <summary>An SNMP GET against an OID in the check's parameters. WP-3.3.</summary>
    Snmp,

    /// <summary>A TCP connect to a port. WP-3.8.</summary>
    Tcp,

    /// <summary>An HTTP(S) request with status/content expectations. WP-3.8.</summary>
    Http,

    /// <summary>
    /// A TLS handshake that reports how many days the served certificate has left. WP-3.8.
    /// <para>
    /// Its own type rather than a flag on <see cref="Http"/>: a check carries one warning and one
    /// critical threshold, and days-remaining falls while latency rises, so the two cannot share a
    /// pair. Stored as a string (see <c>CheckDefinitionConfiguration</c>), so this needs no migration.
    /// </para>
    /// </summary>
    Tls,
}

public enum ThresholdComparison
{
    /// <summary>Bad when the value climbs above the threshold — latency, CPU, temperature.</summary>
    GreaterThan,

    /// <summary>Bad when the value falls below it — free disk, free memory, certificate days left.</summary>
    LessThan,
}
