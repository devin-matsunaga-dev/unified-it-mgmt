namespace Modules.Monitoring.Data;

/// <summary>
/// One requested run of a <see cref="ScanProfile"/>, outside whatever schedule that profile carries.
/// <para>
/// The shape is <see cref="RunbookExecution"/>'s and for the same reason. ARCHITECTURE §4 gives an
/// agent publish-only bus credentials and says agents never consume commands, so "scan now" cannot be
/// a message pushed at the scanner. Instead the platform records that somebody asked, and the scanner
/// <em>collects</em> what is waiting for its group on its next cycle under its own <c>CanDiscover</c>
/// identity — the same fetch it already makes for its profile list, against a second endpoint.
/// </para>
/// <para>
/// Consequence, and it is worth saying out loud on the button: a run is queued rather than started. It
/// begins within one scanner cycle, which is <c>DISCOVERY_INTERVAL_SECONDS</c> and thirty seconds in
/// this stack. Nothing here can make that instant without giving the scanner a command channel.
/// </para>
/// </summary>
public sealed class ScanRun
{
    public Guid Id { get; set; }

    public Guid ScanProfileId { get; set; }

    public ScanProfile ScanProfile { get; set; } = null!;

    /// <summary>
    /// The profile's name and group as they were when the run was asked for, copied rather than joined.
    /// A run is a historical fact: renaming or re-grouping the profile afterwards must not rewrite what
    /// was asked for, and the copy is what lets the row survive its profile being deleted.
    /// </summary>
    public required string ScanProfileName { get; set; }

    public required string DiscoveryGroup { get; set; }

    public ScanRunStatus Status { get; set; }

    /// <summary>A person's subject id. There is no automatic requester — nothing but a human asks for one of these.</summary>
    public required string RequestedBy { get; set; }

    public DateTimeOffset RequestedAt { get; set; }

    /// <summary>Which scanner claimed it, set when it is handed over and never afterwards.</summary>
    public string? DiscoveryName { get; set; }

    public DateTimeOffset? DispatchedAt { get; set; }

    /// <summary>
    /// When this stops being waited for. Stamped at dispatch rather than at request, for
    /// <see cref="RunbookExecution.DeadlineAt"/>'s reason: a row that waited an hour for a scanner to
    /// come back has not timed out, it has not started.
    /// </summary>
    public DateTimeOffset? DeadlineAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>
    /// How many addresses the sweep has probed. Written repeatedly while the run is in flight and
    /// finally when it finishes, so it is a progress figure until the run reaches a terminal state and
    /// the total afterwards.
    /// </summary>
    public int? AddressesProbed { get; set; }

    /// <summary>
    /// How many addresses the ranges expanded to, reported once the scanner knows. It is what makes
    /// <see cref="AddressesProbed"/> legible as progress rather than as a bare count, and it can only
    /// be known on the scanner: a profile scanning <c>local</c> has no size until somebody resolves it.
    /// </summary>
    public int? AddressesTotal { get; set; }

    /// <summary>
    /// The last address that answered, carried purely as evidence for somebody watching. Deliberately
    /// <em>not</em> "the address being scanned now": the sweep runs hundreds of probes concurrently, so
    /// there is no such thing, and inventing one would be a comforting fiction.
    /// </summary>
    public string? LastRespondingAddress { get; set; }

    /// <summary>When progress was last reported. A run whose progress has stopped moving is visible.</summary>
    public DateTimeOffset? ProgressAt { get; set; }

    /// <summary>
    /// How many devices answered. Zero is a result and not a failure — a clean sweep of an empty range
    /// is the thing the seeded "Documentation range" profile exists to make watchable.
    /// </summary>
    public int? DevicesFound { get; set; }

    /// <summary>Why it failed, or the ranges that would not expand. Never carries a community string.</summary>
    public string? Error { get; set; }
}

/// <summary>
/// Where a requested scan has got to. The three terminal states are distinct for
/// <see cref="RunbookExecutionStatus"/>'s reason: "it ran and found nothing" and "no scanner ever
/// collected it" read completely differently, and collapsing them would hide a dead scanner behind a
/// quiet network.
/// </summary>
public enum ScanRunStatus
{
    /// <summary>Asked for, waiting for a scanner in its group to collect it.</summary>
    Queued,

    /// <summary>Collected by a scanner, inside its deadline.</summary>
    Running,

    Succeeded,

    Failed,

    /// <summary>The deadline passed with no result. Never re-dispatched; somebody asks again.</summary>
    TimedOut,
}
