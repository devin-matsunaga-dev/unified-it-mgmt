namespace Modules.Monitoring.Features.Runbooks;

/// <summary>
/// The bounds ARCHITECTURE §7 invariant 4 requires of auto-remediation. Every one has a default,
/// because an unconfigured deployment must still be bounded rather than unbounded — the same rule
/// WP-3.6's alert→ticket options follow.
/// </summary>
public sealed class RunbookOptions
{
    public const string SectionName = "Monitoring:Runbooks";

    /// <summary>
    /// The estate-wide off switch, and it is a real one: with this false nothing creates an execution,
    /// automatic or manual, and the poller channel hands out nothing. It is the control somebody
    /// reaches for at three in the morning, so it stops the whole thing rather than half of it.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Whether an alert may start a runbook. Separate from <see cref="Enabled"/> so that automation can
    /// be stood down while an operator keeps the ability to run one deliberately — which is the state
    /// somebody wants while they work out why the automation misfired.
    /// </summary>
    public bool AutomaticTriggersEnabled { get; set; } = true;

    /// <summary>How many pending executions one poller fetch may claim. A batch, not a queue drain.</summary>
    public int DispatchBatchSize { get; set; } = 5;

    public int DefaultTimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// The ceiling on any runbook's timeout. A runbook allowed to run for an hour is a runbook holding
    /// a poller's attention for an hour, and the platform has no way to interrupt one.
    /// </summary>
    public int MaximumTimeoutSeconds { get; set; } = 600;

    public int DefaultMaxExecutionsPerWindow { get; set; } = 5;

    public int DefaultRateLimitWindowMinutes { get; set; } = 60;

    /// <summary>
    /// How much of what a runbook printed is kept. It goes onto a ticket verbatim, so it is bounded
    /// before it is stored rather than at the point it is rendered — a comment nobody can scroll past
    /// is a ticket nobody reads.
    /// </summary>
    public int MaximumOutputCharacters { get; set; } = 8_000;

    /// <summary>How often the timeout sweeper looks for executions nobody ever answered for.</summary>
    public int SweepIntervalSeconds { get; set; } = 30;
}
