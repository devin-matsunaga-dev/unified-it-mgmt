using Microsoft.EntityFrameworkCore;
using Modules.Assets.Data;

namespace Modules.Assets.Features.Relationships;

/// <summary>
/// The recursive-CTE traversals of <c>assets.ci_relationships</c>. Both walks carry the path they
/// took in an array and refuse to re-enter a CI already on it, so a cycle in the data is traversed
/// once and then stops rather than looping forever; <see cref="CiGraphAnalyzer"/> reports afterwards
/// that one was there.
/// </summary>
internal static class CiGraphQuery
{
    /// <summary>Deepest walk the API will perform, whatever the caller asks for.</summary>
    internal const int MaximumDepth = 10;

    internal const int DefaultDepth = 5;

    internal static Task<List<CiGraphHop>> WalkAsync(
        AssetsDbContext dbContext,
        Guid rootCiId,
        CiGraphDirection direction,
        int maxDepth,
        CancellationToken cancellationToken)
    {
        var depth = Math.Clamp(maxDepth, 1, MaximumDepth);

        // The two directions are the same walk with the ends swapped: Ancestors follows source→target
        // (what the root needs), Descendants follows target→source (what needs the root).
        var hops = direction == CiGraphDirection.Ancestors
            ? dbContext.CiGraphHops.FromSqlInterpolated(
                $"""
                 WITH RECURSIVE walk AS (
                     SELECT r.target_ci_id AS ci_id, 1 AS depth,
                            ARRAY[r.source_ci_id, r.target_ci_id] AS visited
                     FROM assets.ci_relationships r
                     WHERE r.source_ci_id = {rootCiId}
                     UNION ALL
                     SELECT r.target_ci_id, w.depth + 1, w.visited || r.target_ci_id
                     FROM assets.ci_relationships r
                     JOIN walk w ON r.source_ci_id = w.ci_id
                     WHERE w.depth < {depth} AND NOT (r.target_ci_id = ANY (w.visited))
                 )
                 SELECT ci_id, MIN(depth) AS depth FROM walk WHERE ci_id <> {rootCiId} GROUP BY ci_id
                 """)
            : dbContext.CiGraphHops.FromSqlInterpolated(
                $"""
                 WITH RECURSIVE walk AS (
                     SELECT r.source_ci_id AS ci_id, 1 AS depth,
                            ARRAY[r.target_ci_id, r.source_ci_id] AS visited
                     FROM assets.ci_relationships r
                     WHERE r.target_ci_id = {rootCiId}
                     UNION ALL
                     SELECT r.source_ci_id, w.depth + 1, w.visited || r.source_ci_id
                     FROM assets.ci_relationships r
                     JOIN walk w ON r.target_ci_id = w.ci_id
                     WHERE w.depth < {depth} AND NOT (r.source_ci_id = ANY (w.visited))
                 )
                 SELECT ci_id, MIN(depth) AS depth FROM walk WHERE ci_id <> {rootCiId} GROUP BY ci_id
                 """);

        return hops.ToListAsync(cancellationToken);
    }
}

/// <summary>Pure graph reasoning over an edge set, kept out of the service so it can be unit-tested.</summary>
public static class CiGraphAnalyzer
{
    /// <summary>
    /// Whether the edges contain a directed cycle. The traversal already refuses to loop, so this
    /// only reports what the data looks like — a caller may want to warn about it.
    /// </summary>
    public static bool ContainsCycle(IReadOnlyCollection<CiGraphEdge> edges)
    {
        var outgoing = edges
            .GroupBy(edge => edge.SourceCiId)
            .ToDictionary(group => group.Key, group => group.Select(edge => edge.TargetCiId).ToArray());
        var settled = new HashSet<Guid>();
        var onStack = new HashSet<Guid>();

        foreach (var start in outgoing.Keys)
        {
            if (settled.Contains(start))
            {
                continue;
            }

            // Iterative depth-first search: a node still on the stack when we reach it again closes
            // a cycle. Recursion would risk a stack overflow on a deep chain.
            var stack = new Stack<(Guid Node, int Next)>();
            stack.Push((start, 0));
            onStack.Add(start);

            while (stack.Count > 0)
            {
                var (node, next) = stack.Pop();
                var targets = outgoing.TryGetValue(node, out var found) ? found : [];
                if (next == targets.Length)
                {
                    onStack.Remove(node);
                    settled.Add(node);
                    continue;
                }

                stack.Push((node, next + 1));
                var target = targets[next];
                if (onStack.Contains(target))
                {
                    return true;
                }

                if (!settled.Contains(target))
                {
                    stack.Push((target, 0));
                    onStack.Add(target);
                }
            }
        }

        return false;
    }
}
