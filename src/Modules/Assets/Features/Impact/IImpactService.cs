namespace Modules.Assets.Features.Impact;

public interface IImpactService
{
    /// <summary>
    /// What breaks if this CI dies: the CIs that depend on it, what is already open on them, what that
    /// is costing against the SLA, and whose work it is. Null when the CI does not exist.
    /// </summary>
    Task<ImpactResponse?> GetImpactAsync(Guid ciId, int maxDepth, CancellationToken cancellationToken);
}
