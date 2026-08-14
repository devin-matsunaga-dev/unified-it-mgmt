using Microsoft.EntityFrameworkCore;

using Modules.Assets.Data;

using Platform.Integration;

namespace Modules.Assets.Features.Relationships;

/// <summary>
/// Assets' side of <see cref="ICiDependencyDirectory"/>: the dependency edges among a set of CIs that
/// are all currently alerting, which is what lets Monitoring tell a cause from a consequence.
/// <para>
/// One statement for the whole set rather than a walk per CI. The alert engine asks this on any batch
/// that raises something, and a burst is exactly the case where the set is largest — a query per bad
/// device would make a estate-wide outage the most expensive thing the platform does.
/// </para>
/// </summary>
public sealed class CiDependencyDirectory(AssetsDbContext dbContext) : ICiDependencyDirectory
{
    public async Task<IReadOnlyList<CiDependencyLink>> GetDependenciesAmongAsync(
        IReadOnlyCollection<Guid> ciIds,
        int maxDepth,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ciIds);

        // One CI cannot depend on itself, and the caller asking about one thing is the common case on
        // a healthy estate — a round trip to learn "no" is worth skipping.
        if (ciIds.Count < 2)
        {
            return [];
        }

        var ids = ciIds.Distinct().ToArray();
        var depth = Math.Clamp(maxDepth, 1, CiGraphQuery.MaximumDepth);

        // The Ancestors walk of CiGraphQuery, seeded from every id at once and carrying the root it
        // started from. Following WP-2.3: source needs target, so walking source→target walks toward
        // what a CI depends on, and an ancestor that is also failing is the better explanation.
        //
        // `visited` carries the path and refuses to re-enter it, so a cycle among mutually dependent
        // CIs is traversed once and stops. That matters more here than on the API traversals: a
        // clustered pair alerting together is a real estate shape, and this must terminate on it.
        var hops = await dbContext.CiDependencyHops
            .FromSqlInterpolated(
                $"""
                 WITH RECURSIVE walk AS (
                     SELECT r.source_ci_id AS root, r.target_ci_id AS ci_id, 1 AS depth,
                            ARRAY[r.source_ci_id, r.target_ci_id] AS visited
                     FROM assets.ci_relationships r
                     WHERE r.source_ci_id = ANY ({ids})
                     UNION ALL
                     SELECT w.root, r.target_ci_id, w.depth + 1, w.visited || r.target_ci_id
                     FROM assets.ci_relationships r
                     JOIN walk w ON r.source_ci_id = w.ci_id
                     WHERE w.depth < {depth} AND NOT (r.target_ci_id = ANY (w.visited))
                 )
                 SELECT root AS ci_id, ci_id AS depends_on_ci_id, MIN(depth) AS depth
                 FROM walk
                 WHERE ci_id = ANY ({ids}) AND ci_id <> root
                 GROUP BY root, ci_id
                 """)
            .ToListAsync(cancellationToken);

        return [.. hops.Select(hop => new CiDependencyLink(hop.CiId, hop.DependsOnCiId, hop.Depth))];
    }
}
