namespace Modules.Monitoring.Data;

/// <summary>
/// The durable record of one alert. Redis holds the state machine's working state (ARCHITECTURE §5),
/// but Redis is explicitly not a source of truth, so what an operator is shown and what WP-3.6 opens
/// a ticket from lives here and survives a flush. The Redis state is rebuilt from the open row when
/// it is missing, which is what makes a flush a hiccup rather than a storm of re-raised alerts.
/// </summary>
public sealed class Alert
{
    public Guid Id { get; set; }

    public Guid DeviceId { get; set; }

    public MonitoredDevice Device { get; set; } = null!;

    public Guid CiId { get; set; }

    public Guid CheckId { get; set; }

    /// <summary>
    /// What is wrong, in a form stable across restarts: <c>check:{checkId}:availability</c> or
    /// <c>check:{checkId}:{metric}</c>. Device-scoped, because WP-3.6's dedupe key is
    /// <c>alert:{deviceId}:{ruleId}</c>.
    /// </summary>
    public required string RuleId { get; set; }

    /// <summary>The metric the rule watches, or <c>check.success</c> for an availability rule.</summary>
    public required string MetricName { get; set; }

    public AlertSeverity Severity { get; set; }

    public AlertStatus Status { get; set; }

    public required string Summary { get; set; }

    public double? LastValue { get; set; }

    /// <summary>The configured threshold that was crossed; null for an availability rule.</summary>
    public double? Threshold { get; set; }

    public int ConsecutiveBreaches { get; set; }

    /// <summary>
    /// True while the rule is changing state faster than the flap policy allows. A flapping alert is
    /// recorded and shown but publishes nothing, so a board can say "this is flapping" rather than a
    /// consumer having to infer it from a burst of messages that were never sent.
    /// </summary>
    public bool IsFlapping { get; set; }

    /// <summary>Why this alert published nothing, if it did not. <see cref="AlertSuppression.None"/> otherwise.</summary>
    public AlertSuppression Suppression { get; set; }

    public DateTimeOffset RaisedAt { get; set; }

    public DateTimeOffset LastObservedAt { get; set; }

    public DateTimeOffset? ClearedAt { get; set; }

    /// <summary>
    /// When somebody said they were dealing with this, or null while nobody has. WP-3.9.
    /// <para>
    /// An acknowledgement is an annotation and nothing more: it does not suppress the alert, does not
    /// reach the state machine, and does not travel to WP-3.6's ticket automation. That is deliberate
    /// — the alert row's severity states what is true about the estate, and "a human has seen it"
    /// must not be able to change that. The only thing it changes is what a board shows.
    /// </para>
    /// <para>
    /// It also does not survive a clear: a recurrence opens a new row (see <see cref="AlertStatus"/>),
    /// so the same problem coming back is unacknowledged again, which is the point of the button.
    /// </para>
    /// </summary>
    public DateTimeOffset? AcknowledgedAt { get; set; }

    /// <summary>The immutable identity id of whoever acknowledged, for the audit trail.</summary>
    public string? AcknowledgedBy { get; set; }

    /// <summary>
    /// Their display name at the time, snapshotted beside the id following the WP-1.7 comment-author
    /// precedent — a board has to print a name without asking an identity provider per row, and it has
    /// to keep printing one after that person leaves the directory.
    /// </summary>
    public string? AcknowledgedByName { get; set; }

    public required string PollerName { get; set; }
}

/// <summary>
/// How bad a rule is. Ordered, and the order is load-bearing: the state machine compares severities
/// to tell an escalation from a recovery.
/// </summary>
public enum AlertSeverity
{
    Ok = 0,
    Warning = 1,
    Critical = 2,
}

public enum AlertStatus
{
    /// <summary>Currently bad. At most one open alert exists per (device, rule) — a filtered unique index says so.</summary>
    Open,

    /// <summary>Recovered. Kept as history; a recurrence opens a new row rather than reviving this one.</summary>
    Cleared,
}

public enum AlertSuppression
{
    None,

    /// <summary>Inside an active maintenance window. Evaluated and recorded; nothing published.</summary>
    Maintenance,

    /// <summary>Changing state too often to be worth reporting. Evaluated and recorded; nothing published.</summary>
    Flapping,
}
