using Modules.Assets.Data;
using Modules.Assets.Features.Cis;

namespace Modules.Assets.Features.Lifecycle;

public sealed record TransitionCiRequest(CiLifecycleState TargetState, string? Note = null);

/// <summary>
/// A full statement of ownership: every member is replaced by what is sent, so omitting the owner
/// checks the CI back in rather than leaving the previous holder behind.
/// </summary>
public sealed record AssignCiRequest(
    Guid? OwnerUserId = null,
    Guid? DepartmentId = null,
    Guid? SiteId = null,
    string? Note = null);

public sealed record CiLifecycleHistoryResponse(
    Guid Id,
    Guid CiId,
    CiLifecycleState FromState,
    CiLifecycleState ToState,
    string? Note,
    string ActorId,
    DateTimeOffset OccurredAt);

public sealed record CiAssignmentResponse(
    Guid Id,
    Guid CiId,
    CiAssignmentAction Action,
    Guid? FromOwnerUserId,
    string? FromOwnerName,
    Guid? ToOwnerUserId,
    string? ToOwnerName,
    Guid? DepartmentId,
    string? DepartmentName,
    Guid? SiteId,
    string? SiteName,
    string? Note,
    string ActorId,
    DateTimeOffset OccurredAt);

/// <summary>The lifecycle graph as the UI needs it: every state with the states it may move to.</summary>
public sealed record CiLifecycleStateResponse(
    CiLifecycleState State,
    IReadOnlyList<CiLifecycleState> AllowedTargets);

public sealed record CiLifecycleResult(
    CiOutcome Outcome,
    CiResponse? Ci = null,
    IReadOnlyDictionary<string, string[]>? Errors = null,
    string? Error = null);
