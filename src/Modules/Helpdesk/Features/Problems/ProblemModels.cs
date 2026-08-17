using Modules.Helpdesk.Data;

namespace Modules.Helpdesk.Features.Problems;

/// <summary>
/// How a problem write ended.
/// <para>
/// <see cref="InvalidTransition"/> is its own member rather than folded into <see cref="Invalid"/>
/// because the two are different answers: a missing workaround is a 400 about the request, while asking
/// a closed problem to become a known error is a 409 about the state it is in. WP-5.6 drew the same line
/// between "wrong" and "not now".
/// </para>
/// </summary>
public enum ProblemOutcome
{
    Success,
    NotFound,
    Invalid,
    InvalidTransition,
    Duplicate,
    Forbidden,
}

// ---- problems ----

/// <param name="IncidentIds">
/// Incidents to attach as the problem is opened. Optional: a problem raised from a hunch has none yet,
/// and one accepted from a suggestion arrives with every incident the pass counted.
/// </param>
public sealed record CreateProblemRequest(
    string Title,
    string Description,
    TicketPriority Priority = TicketPriority.Medium,
    Guid? CiId = null,
    Guid? CategoryId = null,
    string? RootCause = null,
    string? Workaround = null,
    string? AssignedTechnicianId = null,
    IReadOnlyList<Guid>? IncidentIds = null);

/// <summary>
/// The editable face of a problem. Status is absent on purpose — it moves through
/// <c>POST /api/problems/{id}/transitions</c>, following the ticket workflow, because a state change has
/// entry conditions that a field assignment would walk straight past.
/// </summary>
public sealed record UpdateProblemRequest(
    string Title,
    string Description,
    TicketPriority Priority,
    Guid? CiId = null,
    Guid? CategoryId = null,
    string? RootCause = null,
    string? Workaround = null,
    string? AssignedTechnicianId = null);

/// <param name="Resolution">Required to reach <see cref="ProblemStatus.Resolved"/> or <see cref="ProblemStatus.Closed"/>.</param>
public sealed record ProblemTransitionRequest(ProblemStatus TargetStatus, string? Resolution = null);

/// <param name="Subject">
/// The CI or category this problem is about, resolved live. Null when the problem names neither, and
/// null-named when the CI has since been deleted — a problem outlives the thing it was about.
/// </param>
public sealed record ProblemResponse(
    Guid Id,
    string Number,
    string Title,
    string Description,
    ProblemStatus Status,
    TicketPriority Priority,
    bool IsKnownError,
    ProblemSubjectResponse? Subject,
    string? RootCause,
    string? Workaround,
    string? Resolution,
    string? AssignedTechnicianId,
    string OpenedById,
    string OpenedByName,
    int IncidentCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? KnownErrorAt,
    DateTimeOffset? ResolvedAt,
    DateTimeOffset? ClosedAt,
    IReadOnlyList<ProblemIncidentResponse>? Incidents = null);

/// <param name="Name">Read at request time, never snapshotted — WP-2.4's rule, and the reason a rename reaches every problem at once.</param>
public sealed record ProblemSubjectResponse(ProblemSuggestionScope Scope, Guid Id, string? Name, string? Type);

public sealed record ProblemIncidentResponse(
    Guid TicketId,
    string Number,
    string Title,
    string Status,
    TicketPriority Priority,
    DateTimeOffset CreatedAt,
    string LinkedById,
    string LinkedByName,
    DateTimeOffset LinkedAt);

public sealed record ProblemPageResponse(IReadOnlyList<ProblemResponse> Items, int Total, int Page, int PageSize);

public sealed record ProblemResult(
    ProblemOutcome Outcome,
    ProblemResponse? Problem = null,
    string? Error = null,
    IReadOnlyDictionary<string, string[]>? Errors = null);

/// <summary>
/// What a transition answers with. A shape of its own rather than a bare <see cref="ProblemResponse"/>,
/// because closing a problem is the one moment the platform has something extra to say — the knowledge
/// article draft the WP asks it to prompt for. Carrying it inline rather than making the browser fetch it
/// afterwards means the prompt cannot be lost to a second request that fails.
/// </summary>
public sealed record ProblemTransitionResult(
    ProblemOutcome Outcome,
    ProblemResponse? Problem = null,
    KnowledgeDraftResponse? KnowledgeDraft = null,
    string? Error = null,
    IReadOnlyDictionary<string, string[]>? Errors = null);

/// <summary>
/// The article somebody would write about this problem, composed from what they already typed.
/// <para>
/// A draft and not an article: WP-5.9 owns the knowledge base and nothing here stores one. What this is
/// for is the moment the WP names — closing a problem prompts for the article — and the prompt is worth
/// far more pre-filled than empty, because the person closing it has just finished writing every field it
/// needs.
/// </para>
/// </summary>
/// <param name="Symptoms">
/// What people actually reported, most frequent first. Distinct incident titles rather than all of them,
/// because eleven incidents on one switch are usually three sentences said eleven times.
/// </param>
public sealed record KnowledgeDraftResponse(
    Guid ProblemId,
    string ProblemNumber,
    string Title,
    string? SubjectName,
    IReadOnlyList<KnowledgeDraftSymptom> Symptoms,
    string? RootCause,
    string? Workaround,
    string? Resolution,
    IReadOnlyList<string> IncidentNumbers);

public sealed record KnowledgeDraftSymptom(string Text, int IncidentCount);

/// <summary>
/// The problem list's filter. Every member optional, following <c>TicketListFilter</c>.
/// </summary>
public sealed record ProblemListFilter(
    string? Search = null,
    IReadOnlyList<ProblemStatus>? Statuses = null,
    bool KnownErrorsOnly = false,
    Guid? CiId = null,
    Guid? CategoryId = null,
    int Page = 1,
    int PageSize = 25);

// ---- suggestions ----

public sealed record ProblemSuggestionResponse(
    Guid Id,
    ProblemSuggestionScope Scope,
    ProblemSubjectResponse Subject,
    int IncidentCount,
    DateTimeOffset WindowStart,
    DateTimeOffset WindowEnd,
    ProblemSuggestionStatus Status,
    DateTimeOffset DetectedAt,
    Guid? CreatedProblemId,
    string? CreatedProblemNumber,
    string? ResolvedById,
    string? ResolvedByName,
    DateTimeOffset? ResolvedAt,
    string? DismissReason,
    IReadOnlyList<ProblemIncidentResponse>? Incidents = null);

/// <summary>
/// Everything is optional: accepting a suggestion unchanged is one click, and the defaults are composed
/// from what the pass counted.
/// </summary>
public sealed record AcceptProblemSuggestionRequest(
    string? Title = null,
    string? Description = null,
    TicketPriority? Priority = null);

public sealed record DismissProblemSuggestionRequest(string? Reason = null);

public sealed record ProblemSuggestionResult(
    ProblemOutcome Outcome,
    ProblemSuggestionResponse? Suggestion = null,
    string? Error = null,
    IReadOnlyDictionary<string, string[]>? Errors = null);

/// <summary>What one pass of the detector did, which is what the manual run answers with and what the job logs.</summary>
/// <param name="Examined">Subjects that carried at least one incident in the window.</param>
/// <param name="Suggested">Suggestions written.</param>
/// <param name="Skipped">Why the rest were not: keyed by the decision, valued by how many.</param>
public sealed record ProblemDetectionRunResponse(
    DateTimeOffset WindowStart,
    DateTimeOffset WindowEnd,
    int MinimumIncidents,
    int Examined,
    int Suggested,
    IReadOnlyDictionary<string, int> Skipped,
    IReadOnlyList<ProblemSuggestionResponse> Suggestions);
