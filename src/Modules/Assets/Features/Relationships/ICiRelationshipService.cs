using System.Security.Claims;

namespace Modules.Assets.Features.Relationships;

public interface ICiRelationshipService
{
    Task<CiRelationshipsResponse?> GetForCiAsync(Guid ciId, CancellationToken cancellationToken);

    Task<CiRelationshipResult> CreateAsync(
        Guid sourceCiId,
        CreateCiRelationshipRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken);

    Task<CiRelationshipOutcome> DeleteAsync(
        Guid relationshipId,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken);

    /// <summary>Walks the graph from one CI in the given direction; null when the CI does not exist.</summary>
    Task<CiGraphResponse?> GetGraphAsync(
        Guid ciId,
        CiGraphDirection direction,
        int maxDepth,
        CancellationToken cancellationToken);

    /// <summary>
    /// The blast radius if this CI fails: every CI that depends on it, plus the CI itself at depth 0.
    /// </summary>
    Task<CiGraphResponse?> GetImpactedByAsync(Guid ciId, int maxDepth, CancellationToken cancellationToken);
}
