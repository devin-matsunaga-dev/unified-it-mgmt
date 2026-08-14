namespace Modules.Assets.Features.Topology;

/// <summary>The estate as a drawable graph: CIs, the relationships between them, and what a scan saw.</summary>
public interface ITopologyService
{
    Task<TopologyResponse> GetAsync(TopologyRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Just the neighbour half: every cable a scan observed, folded by pair and each marked with
    /// whether a recorded relationship already says the same thing.
    /// <para>
    /// Exposed for WP-4.6's drift report, which asks one narrow question of it — which observed links
    /// no relationship records — and has no use for the nodes, the type filter or the rendering budget
    /// <see cref="GetAsync"/> exists to apply. Two callers of one reconciliation, rather than a second
    /// copy of the ladder that resolves a neighbour's far end.
    /// </para>
    /// </summary>
    Task<TopologyReconciliation> ReconcileObservedLinksAsync(CancellationToken cancellationToken);
}
