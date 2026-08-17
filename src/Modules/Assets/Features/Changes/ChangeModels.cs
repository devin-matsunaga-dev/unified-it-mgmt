using Modules.Assets.Data;

namespace Modules.Assets.Features.Changes;

public sealed record CreateChangeRequest(
    string Title,
    string Description,
    DateTimeOffset PlannedStartAt,
    DateTimeOffset PlannedEndAt,
    IReadOnlyList<Guid> CiIds,
    bool IncludeDependents = false);

/// <summary>
/// A complete statement of the change, following WP-2.2's assignment endpoint: what is not in the
/// payload is not on the change. A partial update would give no way to say "this no longer touches the
/// database server".
/// </summary>
public sealed record UpdateChangeRequest(
    string Title,
    string Description,
    DateTimeOffset PlannedStartAt,
    DateTimeOffset PlannedEndAt,
    IReadOnlyList<Guid> CiIds,
    bool IncludeDependents = false);

public sealed record ChangeTransitionRequest(ChangeRequestStatus TargetStatus, string? Note = null);

/// <param name="From">
/// Lower bound on the planned window, inclusive. The calendar asks for a month at a time; both bounds
/// are optional and a change is in range when its window overlaps the range at all, so a three-day
/// change straddling the first of the month appears in both months rather than in neither.
/// </param>
public sealed record ChangeListRequest(
    string? Search = null,
    IReadOnlyList<ChangeRequestStatus>? Statuses = null,
    Guid? CiId = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    int Page = 1,
    int PageSize = 25);

/// <param name="Name">Null when the CI has been deleted — which the delete guard prevents, so it is a belt.</param>
public sealed record ChangeCiResponse(
    Guid CiId,
    string? Name,
    string? Type,
    string? AssetTag,
    string? LifecycleState,
    bool IsDependent);

/// <param name="DependentCount">How many of the CIs the dependency walk added. Zero until approval.</param>
/// <param name="NextStatuses">
/// What this change can become next, so a board does not have to hold its own copy of the workflow.
/// </param>
/// <param name="Cis">Only on a single-change read; the list leaves it null.</param>
public sealed record ChangeResponse(
    Guid Id,
    string Number,
    string Title,
    string Description,
    ChangeRequestStatus Status,
    DateTimeOffset PlannedStartAt,
    DateTimeOffset PlannedEndAt,
    bool IncludeDependents,
    string RequestedById,
    string RequestedByName,
    DateTimeOffset RequestedAt,
    string? DecidedById,
    string? DecidedByName,
    DateTimeOffset? DecidedAt,
    string? DecisionNote,
    DateTimeOffset UpdatedAt,
    int CiCount,
    int DependentCount,
    IReadOnlyList<ChangeRequestStatus> NextStatuses,
    IReadOnlyList<ChangeCiResponse>? Cis = null);

public sealed record ChangePageResponse(
    IReadOnlyList<ChangeResponse> Items,
    int Total,
    int Page,
    int PageSize);

public enum ChangeOutcome
{
    Success,
    NotFound,
    Invalid,
    InvalidTransition,
    Forbidden,
}

public sealed record ChangeResult(
    ChangeOutcome Outcome,
    ChangeResponse? Change = null,
    IReadOnlyDictionary<string, string[]>? Errors = null,
    string? Error = null);
