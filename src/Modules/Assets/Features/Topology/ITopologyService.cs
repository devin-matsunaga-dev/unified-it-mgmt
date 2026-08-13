namespace Modules.Assets.Features.Topology;

/// <summary>The estate as a drawable graph: CIs, the relationships between them, and what a scan saw.</summary>
public interface ITopologyService
{
    Task<TopologyResponse> GetAsync(TopologyRequest request, CancellationToken cancellationToken);
}
