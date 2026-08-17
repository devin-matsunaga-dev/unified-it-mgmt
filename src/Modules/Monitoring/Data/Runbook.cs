namespace Modules.Monitoring.Data;

/// <summary>
/// One allowlisted remediation, registered into this estate.
/// <para>
/// Read the word "registered" literally, because it is the whole security shape of WP-5.6. A row here
/// does not contain a script, a command, a path or an argument — it names a
/// <see cref="Key"/> from <c>RunbookCatalog</c>, which is a closed list compiled into the server, and
/// the agent that runs it holds the implementation. So the worst a compromised row can do is ask for
/// something already allowlisted, with parameters already validated against that runbook's schema.
/// ARCHITECTURE §7 invariant 4 says no free-text execution path exists anywhere; this is the table
/// that would have been the obvious place to introduce one.
/// </para>
/// <para>
/// A runbook is deliberately not a poller-config entity and writes no <c>config_changes</c> row: the
/// poller is told what to run one execution at a time, when there is one, rather than holding a
/// catalogue of things it might be asked for.
/// </para>
/// </summary>
public sealed class Runbook
{
    public Guid Id { get; set; }

    /// <summary>
    /// The catalogue key — <c>restart-service</c>. Unique, immutable after creation, and the only
    /// string that ever crosses to an agent as an instruction. An agent that does not recognise one
    /// refuses it, which is what makes the allowlist enforced at both ends rather than only at this one.
    /// </summary>
    public required string Key { get; set; }

    /// <summary>What an operator calls it. Cosmetic — nothing dispatches on it.</summary>
    public required string Name { get; set; }

    public string? Description { get; set; }

    /// <summary>
    /// Bumped on every edit, and stamped onto every execution. That is the whole of "versioned": the
    /// audit trail already holds the before/after of each edit (invariant 1), so a second history table
    /// would be a duplicate of it — this number is what lets an execution say which definition it ran
    /// under without joining to that history.
    /// </summary>
    public int Version { get; set; } = 1;

    /// <summary>
    /// How long one execution may take before the platform stops waiting and calls it timed out. The
    /// agent enforces the same number itself; this side enforces it because an agent that has died
    /// enforces nothing.
    /// </summary>
    public int TimeoutSeconds { get; set; }

    /// <summary>Executions of this runbook allowed inside <see cref="RateLimitWindowMinutes"/>.</summary>
    public int MaxExecutionsPerWindow { get; set; }

    public int RateLimitWindowMinutes { get; set; }

    /// <summary>
    /// A disabled runbook stays registered, keeps its history, and executes nothing — manually or
    /// automatically. It is the per-runbook off switch; <c>RunbookOptions.Enabled</c> is the estate-wide one.
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    public required string CreatedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public required string UpdatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public ICollection<RunbookTrigger> Triggers { get; set; } = [];
}

/// <summary>
/// "When this kind of alert is raised, run that runbook with these arguments."
/// <para>
/// The arguments live here rather than on the alert, and that is the second half of the security
/// shape: nothing an alert carries — a summary a device wrote, a metric name, a value — is ever used
/// as a parameter. An operator chose these when they wrote the trigger, they were validated against
/// the runbook's schema then, and they are validated again at dispatch. A device cannot influence what
/// runs on its behalf by changing what it says.
/// </para>
/// </summary>
public sealed class RunbookTrigger
{
    public Guid Id { get; set; }

    public Guid RunbookId { get; set; }

    public Runbook Runbook { get; set; } = null!;

    /// <summary>
    /// Which alerts this matches, by the metric the rule watches — <c>check.success</c> for an
    /// availability rule, or a named metric for a threshold one. Matched case-insensitively and exactly.
    /// <para>
    /// The metric rather than the rule id, because a rule id contains the check's own Guid: a trigger
    /// keyed on one would match exactly one check on exactly one device and would have to be rewritten
    /// every time a check was recreated. "What went wrong" is the durable half of a rule id.
    /// </para>
    /// </summary>
    public required string MetricName { get; set; }

    /// <summary>The lowest severity that fires this. A Warning trigger also fires on Critical.</summary>
    public AlertSeverity MinimumSeverity { get; set; } = AlertSeverity.Critical;

    /// <summary>
    /// One device this trigger is confined to, or null for every device in the estate. Null is the
    /// wider and more dangerous setting, so it is the one an operator has to choose deliberately.
    /// </summary>
    public Guid? DeviceId { get; set; }

    /// <summary>
    /// The parameters to run with, as a jsonb object of string values, validated against the runbook's
    /// schema when this trigger was written. Held as jsonb for the reason WP-3.1 held check parameters
    /// that way: the shape belongs to the runbook, and a table of them would need a migration whenever
    /// a runbook declared a new one.
    /// </summary>
    public required string ParametersJson { get; set; }

    public bool IsEnabled { get; set; } = true;

    public required string CreatedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public required string UpdatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// One run of one runbook: what was asked for, who asked, what came back.
/// <para>
/// It is the durable record on both sides of the channel. The poller is handed pending rows and
/// reports against them, so an agent that dies mid-run leaves a row the timeout sweeper can finish,
/// rather than a request nobody can account for.
/// </para>
/// </summary>
public sealed class RunbookExecution
{
    public Guid Id { get; set; }

    public Guid RunbookId { get; set; }

    public Runbook Runbook { get; set; } = null!;

    /// <summary>
    /// The key and version as they were when this was requested, copied rather than joined. An
    /// execution is a historical fact: editing the runbook afterwards must not rewrite what ran.
    /// </summary>
    public required string RunbookKey { get; set; }

    public int RunbookVersion { get; set; }

    /// <summary>Which trigger fired this, or null for a manual execution.</summary>
    public Guid? TriggerId { get; set; }

    /// <summary>
    /// The alert this remediates, or null for a manual one. Unique per runbook where it is set, which
    /// is what makes "one execution per alert" a database constraint: an alert that escalates
    /// Warning → Critical keeps its alert id, so the escalation cannot start a second run.
    /// </summary>
    public Guid? AlertId { get; set; }

    public Guid DeviceId { get; set; }

    public Guid CiId { get; set; }

    /// <summary>The alert's rule, carried so the completion event can find the ticket. Null for a manual run.</summary>
    public string? RuleId { get; set; }

    /// <summary>The validated parameters, snapshotted as jsonb at request time.</summary>
    public required string ParametersJson { get; set; }

    public RunbookExecutionStatus Status { get; set; }

    /// <summary>The identity that asked. <c>system:monitoring</c> for a trigger, a person's subject id for a manual run.</summary>
    public required string RequestedBy { get; set; }

    public DateTimeOffset RequestedAt { get; set; }

    /// <summary>Which poller claimed it, set when it is handed over and never afterwards.</summary>
    public string? PollerName { get; set; }

    public DateTimeOffset? DispatchedAt { get; set; }

    /// <summary>
    /// When this stops being waited for. Set at dispatch rather than at request, because the clock the
    /// timeout measures is the agent's — a row that waited an hour for a poller to come back has not
    /// timed out, it has not started.
    /// </summary>
    public DateTimeOffset? DeadlineAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public int? ExitCode { get; set; }

    /// <summary>What it printed, truncated on the way in. Never a secret: a runbook is given no credential.</summary>
    public string? Output { get; set; }

    public string? Error { get; set; }
}

/// <summary>
/// Where an execution has got to. The three terminal states are distinct on purpose: "it ran and said
/// no" and "nobody ever answered" call for different reading, and collapsing them would hide a poller
/// that has stopped fetching behind a runbook that appears to keep failing.
/// </summary>
public enum RunbookExecutionStatus
{
    /// <summary>Requested, waiting for a poller to claim it.</summary>
    Pending,

    /// <summary>Handed to a poller, inside its deadline.</summary>
    Dispatched,

    Succeeded,

    Failed,

    /// <summary>The deadline passed with no result. Escalated exactly like a failure, and never retried.</summary>
    TimedOut,
}
