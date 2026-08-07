using Modules.Assets.Data;

namespace Modules.Assets.Features.Relationships;

public sealed record CreateCiRelationshipRequest(
    Guid TargetCiId,
    CiRelationshipType Type,
    string? Description = null);

/// <summary>One edge as the API renders it, with both ends named so a list needs no second call.</summary>
public sealed record CiRelationshipResponse(
    Guid Id,
    Guid SourceCiId,
    string SourceCiName,
    CiType SourceCiType,
    Guid TargetCiId,
    string TargetCiName,
    CiType TargetCiType,
    CiRelationshipType Type,
    string? Description,
    string CreatedBy,
    DateTimeOffset CreatedAt);

/// <summary>
/// A CI's direct edges, split by which end it sits on. <see cref="Upstream"/> is what it depends on,
/// <see cref="Downstream"/> is what depends on it.
/// </summary>
public sealed record CiRelationshipsResponse(
    Guid CiId,
    IReadOnlyList<CiRelationshipResponse> Upstream,
    IReadOnlyList<CiRelationshipResponse> Downstream);

/// <summary>Which way a traversal walks the edges.</summary>
public enum CiGraphDirection
{
    /// <summary>Source→target: everything the CI depends on, however far up the chain.</summary>
    Ancestors = 1,

    /// <summary>Target→source: everything that depends on the CI, however far down the chain.</summary>
    Descendants = 2,
}

/// <summary>A CI the traversal reached, and the fewest hops from the root it took to get there.</summary>
public sealed record CiGraphNode(
    Guid Id,
    CiType Type,
    string Name,
    string? AssetTag,
    CiLifecycleState LifecycleState,
    bool IsActive,
    int Depth);

public sealed record CiGraphEdge(
    Guid Id,
    Guid SourceCiId,
    Guid TargetCiId,
    CiRelationshipType Type);

/// <summary>
/// The result of one traversal. <see cref="Nodes"/> excludes the root unless the caller asked for an
/// impact set; <see cref="Edges"/> holds every edge between the reached CIs, including any that
/// closes a cycle, so a client can draw the graph exactly as it is.
/// </summary>
public sealed record CiGraphResponse(
    Guid RootCiId,
    CiGraphDirection Direction,
    int MaxDepth,
    bool MaxDepthReached,
    bool ContainsCycle,
    IReadOnlyList<CiGraphNode> Nodes,
    IReadOnlyList<CiGraphEdge> Edges);

public enum CiRelationshipOutcome
{
    Success,
    CiNotFound,
    RelationshipNotFound,
    InvalidTarget,
    Duplicate,
    Disposed,
}

public sealed record CiRelationshipResult(
    CiRelationshipOutcome Outcome,
    CiRelationshipResponse? Relationship = null,
    IReadOnlyDictionary<string, string[]>? Errors = null,
    string? Error = null);
