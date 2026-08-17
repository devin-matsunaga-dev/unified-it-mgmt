namespace Modules.Helpdesk.Data;

/// <summary>
/// The cause behind several incidents, and — once somebody has found it — the known error that says what
/// is wrong and what to do about it until it is fixed for good.
/// <para>
/// Owned by Helpdesk because ARCHITECTURE §3 puts tickets and knowledge here, and because every one of a
/// problem's incidents is a row in this schema. The CI it concerns is stored as a bare id for the same
/// reason <see cref="TicketCiLink"/> stores one: schemas do not join, and the name is read live through
/// <c>ICiDirectory</c> so a renamed switch reaches every problem at once.
/// </para>
/// </summary>
public sealed class Problem
{
    public Guid Id { get; set; }
    public long SequenceNumber { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public ProblemStatus Status { get; set; }
    public TicketPriority Priority { get; set; }

    /// <summary>An <c>assets.cis</c> id, when the recurrence is about one thing. Deliberately not a foreign key.</summary>
    public Guid? CiId { get; set; }

    public Guid? CategoryId { get; set; }
    public TicketCategory? Category { get; set; }

    /// <summary>
    /// Why it happens. Required before <see cref="ProblemStatus.KnownError"/>, because a known error
    /// without a cause is an open problem with a longer name.
    /// </summary>
    public string? RootCause { get; set; }

    /// <summary>
    /// What to do about it in the meantime. Required alongside <see cref="RootCause"/> for the same
    /// reason: the workaround is the whole value a known error has to somebody holding a fresh incident.
    /// </summary>
    public string? Workaround { get; set; }

    /// <summary>The permanent fix. Required to resolve or close, following the ticket workflow's resolution note.</summary>
    public string? Resolution { get; set; }

    public string? AssignedTechnicianId { get; set; }
    public string OpenedById { get; set; } = string.Empty;
    public string OpenedByName { get; set; } = string.Empty;

    public ICollection<ProblemIncident> Incidents { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>When the cause was first documented. Cleared if the problem is put back to investigation.</summary>
    public DateTimeOffset? KnownErrorAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }

    public string Number => $"PRB-{SequenceNumber:000000}";
}

/// <summary>
/// Where a problem stands. Deliberately a status rather than a status plus an <c>IsKnownError</c> flag:
/// the two together describe eight states of which most are nonsense — a problem cannot be permanently
/// resolved and still be the known error people are working around.
/// <para>
/// <see cref="KnownError"/> is a state with an entry condition, which is what makes the known-error
/// database a database rather than a checkbox: a problem reaches it only once it carries both a root
/// cause and a workaround.
/// </para>
/// </summary>
public enum ProblemStatus
{
    /// <summary>The cause is not yet known. Where every problem starts.</summary>
    Investigating = 1,

    /// <summary>Cause documented, workaround published, permanent fix outstanding.</summary>
    KnownError = 2,

    /// <summary>Fixed for good, awaiting review.</summary>
    Resolved = 3,

    Closed = 4,
}

/// <summary>
/// One incident this problem explains.
/// <para>
/// A real foreign key on both ends, unlike <see cref="TicketCiLink"/>: a problem and its incidents are
/// both Helpdesk's, so nothing here crosses a schema and the database can keep the link honest.
/// </para>
/// </summary>
public sealed class ProblemIncident
{
    public Guid Id { get; set; }

    public Guid ProblemId { get; set; }
    public Problem Problem { get; set; } = null!;

    public Guid TicketId { get; set; }
    public Ticket Ticket { get; set; } = null!;

    public string LinkedById { get; set; } = string.Empty;
    public string LinkedByName { get; set; } = string.Empty;
    public DateTimeOffset LinkedAt { get; set; }
}

/// <summary>
/// "Five incidents landed on this switch in a week — is that one problem?" A question the nightly pass
/// asks and a human answers.
/// <para>
/// It is a suggestion and never a problem, because the platform can see that incidents cluster and
/// cannot see whether they share a cause. Accepting one creates the problem and links what it counted;
/// dismissing one records that somebody looked.
/// </para>
/// </summary>
public sealed class ProblemSuggestion
{
    public Guid Id { get; set; }

    public ProblemSuggestionScope Scope { get; set; }

    /// <summary>Set when <see cref="Scope"/> is <see cref="ProblemSuggestionScope.Ci"/>, null otherwise.</summary>
    public Guid? CiId { get; set; }

    /// <summary>Set when <see cref="Scope"/> is <see cref="ProblemSuggestionScope.Category"/>, null otherwise.</summary>
    public Guid? CategoryId { get; set; }
    public TicketCategory? Category { get; set; }

    public int IncidentCount { get; set; }
    public DateTimeOffset WindowStart { get; set; }
    public DateTimeOffset WindowEnd { get; set; }

    public ProblemSuggestionStatus Status { get; set; }
    public DateTimeOffset DetectedAt { get; set; }

    /// <summary>The problem somebody made of it. Set once, when the suggestion is accepted.</summary>
    public Guid? CreatedProblemId { get; set; }
    public Problem? CreatedProblem { get; set; }

    public string? ResolvedById { get; set; }
    public string? ResolvedByName { get; set; }

    /// <summary>When somebody accepted or dismissed it. Also what the dismissal cooldown is measured from.</summary>
    public DateTimeOffset? ResolvedAt { get; set; }

    public string? DismissReason { get; set; }

    /// <summary>The CI or the category, whichever this suggestion is about. Never both, never neither.</summary>
    public Guid SubjectId => Scope == ProblemSuggestionScope.Ci
        ? CiId ?? Guid.Empty
        : CategoryId ?? Guid.Empty;
}

/// <summary>What a run of incidents was counted against — the two groupings the WP names.</summary>
public enum ProblemSuggestionScope
{
    /// <summary>One configuration item: the same switch failing over and over.</summary>
    Ci = 1,

    /// <summary>One ticket category: password resets everywhere, on no particular machine.</summary>
    Category = 2,
}

public enum ProblemSuggestionStatus
{
    /// <summary>Waiting for somebody to look at it.</summary>
    Open = 1,

    /// <summary>Somebody made a problem of it. <see cref="ProblemSuggestion.CreatedProblemId"/> says which.</summary>
    Accepted = 2,

    /// <summary>Somebody decided it was not one problem. Suppresses re-detection for the cooldown.</summary>
    Dismissed = 3,
}

/// <summary>
/// The statuses that mean somebody is still working on it.
/// <para>
/// A set compared with <c>Contains</c> rather than a <c>&lt;</c> against <see cref="ProblemStatus.Resolved"/>,
/// because every enum in this solution is stored <c>HasConversion&lt;string&gt;()</c> and EF translates a
/// comparison on one into a comparison of <em>text</em> — the bug WP-5.6 shipped and caught, in which
/// "Critical" sorts before "Warning". The declared ordering of an enum means nothing once its column is a
/// string.
/// </para>
/// </summary>
public static class ProblemStatuses
{
    public static readonly ProblemStatus[] Open = [ProblemStatus.Investigating, ProblemStatus.KnownError];
}
