using System.Security.Claims;

namespace Modules.Assets.Features.Topology;

/// <summary>Saved manual layouts: which CIs an operator has pinned, and where.</summary>
public interface ITopologyMapService
{
    Task<IReadOnlyList<TopologyMapSummaryResponse>> ListAsync(CancellationToken cancellationToken);

    Task<TopologyMapResponse?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<TopologyMapResult> CreateAsync(
        SaveTopologyMapRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken);

    Task<TopologyMapResult> UpdateAsync(
        Guid id,
        SaveTopologyMapRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken);

    Task<TopologyMapOutcome> DeleteAsync(Guid id, ClaimsPrincipal actor, CancellationToken cancellationToken);
}
