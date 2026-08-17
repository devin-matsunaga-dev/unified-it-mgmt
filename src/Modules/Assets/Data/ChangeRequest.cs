namespace Modules.Assets.Data;

/// <summary>
/// A request to disturb named CIs between two instants, and the decision somebody took about it.
/// <para>
/// It lives in Assets rather than in Helpdesk because it is a statement about configuration items, and
/// because "and their dependents" is a walk of <c>assets.ci_relationships</c> — the graph WP-2.3 built.
/// Anywhere else that expansion would be a cross-schema read, which ARCHITECTURE §3 forbids outright.
/// </para>
/// <para>
/// Approving it opens a monitoring maintenance window. That half is not here and cannot be: Monitoring
/// owns windows, this module may not reference it, and a port is never a write path. The approval leaves
/// as <c>ChangeRequestApproved</c> on the bus.
/// </para>
/// </summary>
public sealed class ChangeRequest
{
    public Guid Id { get; set; }

    /// <summary>Database-generated, following the ticket and problem sequences.</summary>
    public long SequenceNumber { get; set; }

    public required string Title { get; set; }

    public required string Description { get; set; }

    public ChangeRequestStatus Status { get; set; } = ChangeRequestStatus.Draft;

    /// <summary>
    /// The agreed start of the work. A planned instant rather than a <c>DateOnly</c> — unlike WP-2.6's
    /// contract dates, a maintenance window is minutes long and its ends are moments, not days.
    /// </summary>
    public DateTimeOffset PlannedStartAt { get; set; }

    public DateTimeOffset PlannedEndAt { get; set; }

    /// <summary>
    /// Whether the window should also cover what depends on the named CIs. Asked at request time and
    /// answered once, at approval, by walking the graph as it stands then — see <see cref="ChangeRequestCi"/>.
    /// </summary>
    public bool IncludeDependents { get; set; }

    public required string RequestedById { get; set; }

    public required string RequestedByName { get; set; }

    public DateTimeOffset RequestedAt { get; set; }

    /// <summary>Null until somebody approves or rejects it. Never the requester — see <c>ChangeWorkflow</c>.</summary>
    public string? DecidedById { get; set; }

    public string? DecidedByName { get; set; }

    public DateTimeOffset? DecidedAt { get; set; }

    /// <summary>Why it was approved, rejected or cancelled, in the decider's words.</summary>
    public string? DecisionNote { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public ICollection<ChangeRequestCi> Cis { get; set; } = [];

    public string Number => $"CHG-{SequenceNumber:000000}";
}

/// <summary>
/// Where a change stands. Deliberately short of a full CAB workflow — WORK_PACKAGES 7.A is where approval
/// boards, freeze windows and rollback plans live, and inventing half of one here would be a shape that
/// package then has to undo.
/// </summary>
public enum ChangeRequestStatus
{
    /// <summary>Being written. The only state in which the schedule and the CI list can still be edited.</summary>
    Draft,

    /// <summary>Waiting for somebody else's decision.</summary>
    Submitted,

    /// <summary>Agreed. The maintenance window has been asked for; whether one exists is Monitoring's answer.</summary>
    Approved,

    /// <summary>Refused. Terminal — a rejected change is re-raised rather than re-argued.</summary>
    Rejected,

    /// <summary>Withdrawn by whoever raised it, before a decision.</summary>
    Cancelled,
}

/// <summary>
/// One CI a change covers.
/// <para>
/// The rows split into two kinds by <see cref="IsDependent"/>: what the requester named, and what the
/// graph added underneath it. The distinction is kept because the two are answerable to different people
/// — somebody reviewing a change needs to see that agreeing to touch a switch also silences eleven hosts
/// — and because only the named half is editable.
/// </para>
/// </summary>
public sealed class ChangeRequestCi
{
    public Guid ChangeRequestId { get; set; }

    public ChangeRequest? ChangeRequest { get; set; }

    public Guid CiId { get; set; }

    public ConfigurationItem? Ci { get; set; }

    /// <summary>
    /// True for a CI the dependency walk added at approval, false for one the requester chose.
    /// <para>
    /// Written once and never refreshed. An edge added after approval does not widen a window that has
    /// already been agreed to, which is the same reason WP-2.11 draws a truncated graph with a flag
    /// rather than quietly extending it.
    /// </para>
    /// </summary>
    public bool IsDependent { get; set; }
}
