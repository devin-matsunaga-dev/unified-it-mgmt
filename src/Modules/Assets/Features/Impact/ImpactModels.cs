using Modules.Assets.Data;

using Platform.Integration;

namespace Modules.Assets.Features.Impact;

/// <summary>
/// One CI inside a blast radius, as <see cref="ImpactAnalyzer"/> sees it. Built by the service from the
/// CI row so the analyzer itself needs no database, no clock and no configuration — the shape
/// <c>DriftAnalyzer</c> established.
/// </summary>
/// <param name="Depth">
/// Hops from the CI that failed; the root itself is zero. The fewest hops, where several routes exist,
/// because the shortest route is the one that explains the impact most directly.
/// </param>
public sealed record ImpactCi(
    Guid CiId,
    string Name,
    CiType Type,
    CiLifecycleState LifecycleState,
    bool IsActive,
    int Depth,
    Guid? OwnerUserId,
    string? OwnerName,
    Guid? DepartmentId,
    string? DepartmentName,
    string? SiteName);

/// <summary>Everything the analyzer needs, and nothing that would make it need a database.</summary>
/// <param name="Root">The CI the question was asked about, at depth 0. Part of its own outage.</param>
/// <param name="TicketTotal">
/// How many open tickets the whole radius really carries, which exceeds <paramref name="Tickets"/> once
/// the directory's cap bites.
/// </param>
public sealed record ImpactSubject(
    ImpactCi Root,
    IReadOnlyList<ImpactCi> Reached,
    IReadOnlyList<ImpactedTicketSummary> Tickets,
    int TicketTotal,
    int MaxDepth,
    bool MaxDepthReached,
    bool ContainsCycle);

/// <summary>
/// The headline numbers, which are the part of this response an operator reads first and often the only
/// part they read at all.
/// </summary>
/// <param name="CiCount">Every CI the outage reaches, the root included.</param>
/// <param name="DirectCiCount">Those one hop away — what fails first, and usually what is worth paging about.</param>
/// <param name="OpenTicketCount">
/// Distinct open tickets across the radius. A ticket linked to two affected CIs is one piece of work.
/// </param>
/// <param name="CisWithoutDepartment">
/// Affected CIs no department owns. Reported rather than bucketed under a made-up "Unassigned"
/// department, because a blast radius that invents an owner is worse than one that admits it has none.
/// </param>
public sealed record ImpactSummaryResponse(
    int CiCount,
    int DirectCiCount,
    int OpenTicketCount,
    int BreachedSlaCount,
    int AtRiskSlaCount,
    DateTimeOffset? NextSlaDueAt,
    int AffectedUserCount,
    int AffectedDepartmentCount,
    int CisWithoutDepartment,
    bool CisTruncated,
    bool TicketsTruncated);

public sealed record ImpactedCiResponse(
    Guid CiId,
    string Name,
    CiType Type,
    CiLifecycleState LifecycleState,
    bool IsActive,
    int Depth,
    Guid? OwnerUserId,
    string? OwnerName,
    Guid? DepartmentId,
    string? DepartmentName,
    string? SiteName,
    int OpenTicketCount);

/// <summary>
/// One open ticket the outage already has behind it, attributed to the affected CI nearest the root.
/// </summary>
public sealed record ImpactedTicketResponse(
    Guid TicketId,
    string Number,
    string Title,
    string Status,
    string Priority,
    DateTimeOffset CreatedAt,
    Guid CiId,
    string CiName,
    SlaExposure? Sla);

/// <summary>A department the outage reaches, and how much of it lands there.</summary>
public sealed record ImpactedDepartmentResponse(
    Guid DepartmentId,
    string Name,
    int CiCount,
    int OpenTicketCount);

/// <summary>
/// A person the outage reaches, by way of the CIs they hold. Named "affected" rather than "notified":
/// nothing here sends anything, and WP-3.10's routing rules remain the only thing that decides who hears.
/// </summary>
public sealed record ImpactedUserResponse(
    Guid UserId,
    string Name,
    int CiCount,
    int OpenTicketCount);

/// <summary>
/// The answer to "what breaks if this dies" — the graph, what is already open on it, what that is
/// costing against the SLA, and who feels it.
/// </summary>
/// <param name="ContainsCycle">
/// True when the affected CIs are mutually dependent somewhere. Each CI is still counted once; the flag
/// exists because a reader comparing this against the relationship tree deserves to know why it is not a
/// tree.
/// </param>
public sealed record ImpactResponse(
    Guid RootCiId,
    string RootCiName,
    CiType RootCiType,
    int MaxDepth,
    bool MaxDepthReached,
    bool ContainsCycle,
    ImpactSummaryResponse Summary,
    IReadOnlyList<ImpactedCiResponse> Cis,
    IReadOnlyList<ImpactedTicketResponse> Tickets,
    IReadOnlyList<ImpactedDepartmentResponse> Departments,
    IReadOnlyList<ImpactedUserResponse> Users);
