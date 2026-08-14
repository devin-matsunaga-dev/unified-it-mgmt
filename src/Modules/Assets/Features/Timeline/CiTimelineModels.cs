using Modules.Assets.Data;

using Platform.Auditing;
using Platform.Integration;

namespace Modules.Assets.Features.Timeline;

/// <summary>
/// The four things that happen to a CI, and the four sources behind them. The names are the filter's
/// vocabulary as well as the response's, so <c>?types=alert,ticket</c> is this enum and nothing else.
/// </summary>
public enum CiTimelineEventKind
{
    /// <summary>Something monitoring found wrong with it. <c>monitoring.alerts</c>, through a port.</summary>
    Alert = 1,

    /// <summary>Somebody raised work about it. <c>helpdesk.ticket_ci_links</c>, through a port.</summary>
    Ticket = 2,

    /// <summary>
    /// It moved through its life or changed hands. <c>assets.ci_lifecycle_history</c> and
    /// <c>assets.ci_assignment_entries</c>, which are Assets' own.
    /// <para>
    /// Both under one kind deliberately: an operator filtering to "lifecycle" wants the state change and
    /// the check-out that went with it, and splitting them would make the commonest filter two clicks.
    /// </para>
    /// </summary>
    Lifecycle = 3,

    /// <summary>Its record was edited. <c>platform.audit_entries</c>, read through Platform.</summary>
    Config = 4,
}

/// <summary>
/// One lifecycle-shaped fact about a CI, covering both of the tables behind
/// <see cref="CiTimelineEventKind.Lifecycle"/>. Built by the service so
/// <see cref="CiTimelineAssembler"/> needs no database — the shape <c>ImpactAnalyzer</c> established.
/// </summary>
/// <param name="Action">Null for a state transition; set for a check-in, check-out, transfer or move.</param>
public sealed record CiLifecycleEvent(
    Guid Id,
    DateTimeOffset OccurredAt,
    string ActorId,
    CiLifecycleState? FromState,
    CiLifecycleState? ToState,
    CiAssignmentAction? Action,
    string? FromOwnerName,
    string? ToOwnerName,
    string? DepartmentName,
    string? SiteName,
    string? Note);

/// <summary>Everything the assembler needs, and nothing that would make it need a database.</summary>
/// <param name="Kinds">
/// What the caller asked for. Sources outside it were never queried, and the response says so rather than
/// reporting them as empty — "you did not ask" and "nothing happened" are different answers.
/// </param>
/// <param name="LifecycleTotal">
/// How many lifecycle facts the window really holds, which exceeds <paramref name="Lifecycle"/> once the
/// cap bites. The two ports carry their own totals; this one is counted by the service.
/// </param>
public sealed record CiTimelineSubject(
    Guid CiId,
    string CiName,
    IReadOnlyList<CiTimelineEventKind> Kinds,
    DateTimeOffset? From,
    DateTimeOffset? To,
    CiAlertHistory Alerts,
    CiTicketHistory Tickets,
    IReadOnlyList<CiLifecycleEvent> Lifecycle,
    int LifecycleTotal,
    AuditTrail Audit);

/// <summary>
/// One thing that happened to the CI, in the shape the axis renders it.
/// <para>
/// <paramref name="Title"/> and <paramref name="Detail"/> are composed here rather than in the browser
/// because composing them needs the source rows — a from-state and a to-state, the list of fields an edit
/// changed — and sending four differently-shaped payloads for the browser to re-sentence would put the
/// same knowledge in two languages. What is deliberately <em>not</em> composed is any timestamp: those
/// travel as instants and are converted at the UI, per CONVENTIONS.
/// </para>
/// </summary>
/// <param name="Id">The source row's own id. Stable across reads, so the browser can key on it.</param>
/// <param name="Actor">Who did it, where a person did. Alerts have none — nobody chose them.</param>
/// <param name="LinkedAt">
/// Set only on a ticket that was attached to this CI materially later than it was raised, which is the
/// case worth pointing at: the entry sits at the moment the ticket was reported, and this says the asset
/// was implicated afterwards.
/// </param>
public sealed record CiTimelineEntryResponse(
    CiTimelineEventKind Kind,
    Guid Id,
    DateTimeOffset OccurredAt,
    string Title,
    string? Detail,
    string? Actor,
    string? Severity,
    string? Status,
    string? Priority,
    Guid? AlertId,
    Guid? DeviceId,
    Guid? TicketId,
    string? TicketNumber,
    DateTimeOffset? LinkedAt);

/// <summary>
/// What one source contributed, and what it was holding back.
/// </summary>
/// <param name="Requested">
/// False when the filter excluded this kind. The source was not queried at all, and its counts are zero
/// because nothing asked rather than because nothing happened.
/// </param>
/// <param name="Total">
/// Everything this source holds inside the window, cap or no cap. The honest number an operator needs
/// before concluding a quiet timeline means a quiet asset — WP-2.4's rule.
/// </param>
public sealed record CiTimelineSourceResponse(
    CiTimelineEventKind Kind,
    bool Requested,
    int Returned,
    int Total,
    bool Truncated);

/// <param name="TotalCount">Across every requested source, including what the caps left out.</param>
/// <param name="EarliestAt">
/// The oldest entry on the axis. Not the oldest thing that ever happened to the CI: once a cap has
/// bitten, everything before this is out of view, which is what <paramref name="Truncated"/> says.
/// </param>
public sealed record CiTimelineSummaryResponse(
    int EntryCount,
    int TotalCount,
    bool Truncated,
    DateTimeOffset? EarliestAt,
    DateTimeOffset? LatestAt);

/// <summary>
/// One CI's history on one axis: what alerted, what was raised, how it moved through its life, and who
/// edited its record — interleaved, newest first.
/// </summary>
/// <param name="Limit">The per-source cap that was applied, echoed so a truncated answer explains itself.</param>
public sealed record CiTimelineResponse(
    Guid CiId,
    string CiName,
    DateTimeOffset? From,
    DateTimeOffset? To,
    int Limit,
    IReadOnlyList<CiTimelineEventKind> Kinds,
    CiTimelineSummaryResponse Summary,
    IReadOnlyList<CiTimelineSourceResponse> Sources,
    IReadOnlyList<CiTimelineEntryResponse> Entries);

/// <summary>What the endpoint asks the service for, after the edge has validated it.</summary>
public sealed record CiTimelineRequest(
    IReadOnlyList<CiTimelineEventKind> Kinds,
    DateTimeOffset? From,
    DateTimeOffset? To,
    int Limit);
