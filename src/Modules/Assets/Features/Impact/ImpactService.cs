using Microsoft.EntityFrameworkCore;

using Modules.Assets.Data;
using Modules.Assets.Features.Relationships;

using Platform.Integration;

namespace Modules.Assets.Features.Impact;

/// <summary>
/// Assembles a blast radius: WP-2.3's downstream walk, the open tickets Helpdesk holds against what it
/// reached, and the ownership the CMDB already records.
/// <para>
/// It writes nothing, and there is nothing here it could write. A blast radius is a reading of what an
/// outage would cost — the decision about what to do with that is the operator's, and every action they
/// might take (open a change, raise a ticket, declare a major incident) already has its own audited
/// endpoint.
/// </para>
/// </summary>
public sealed class ImpactService(
    AssetsDbContext dbContext,
    ITicketImpactDirectory ticketImpactDirectory) : IImpactService
{
    public async Task<ImpactResponse?> GetImpactAsync(
        Guid ciId,
        int maxDepth,
        CancellationToken cancellationToken)
    {
        var root = await LoadAsync(ci => ci.Id == ciId, cancellationToken);
        if (root.Count == 0)
        {
            return null;
        }

        var depth = Math.Clamp(maxDepth, 1, CiGraphQuery.MaximumDepth);

        // The descendants walk: target→source, everything that needs this CI. The same traversal the CI
        // page's "Downstream impact" tree draws, so the panel and the tree can never disagree about who
        // is in the outage.
        var hops = await CiGraphQuery.WalkAsync(
            dbContext, ciId, CiGraphDirection.Descendants, depth, cancellationToken);
        var depthById = hops.Where(hop => hop.CiId != ciId).ToDictionary(hop => hop.CiId, hop => hop.Depth);

        var reachedIds = depthById.Keys.ToList();
        var reached = reachedIds.Count == 0
            ? []
            : await LoadAsync(ci => reachedIds.Contains(ci.Id), cancellationToken);

        // Every edge between the CIs the walk reached, so the cycle report is about the shape of the
        // answer rather than about the whole estate. Read for that flag alone — the panel draws a list,
        // and the tree on the CI page is where the routes are read.
        var scope = reachedIds.Append(ciId).ToList();
        var edges = await dbContext.CiRelationships
            .AsNoTracking()
            .Where(relationship => scope.Contains(relationship.SourceCiId)
                && scope.Contains(relationship.TargetCiId))
            .Select(relationship => new CiGraphEdge(
                relationship.Id, relationship.SourceCiId, relationship.TargetCiId, relationship.Type))
            .ToListAsync(cancellationToken);

        // Asked about the whole radius at once. The cap is the analyzer's, so what the panel renders and
        // what the directory fetches are one number rather than two that drift apart.
        var tickets = await ticketImpactDirectory.GetOpenTicketsForCisAsync(
            scope, ImpactAnalyzer.MaximumTickets, cancellationToken);

        return ImpactAnalyzer.Analyse(new ImpactSubject(
            root[0] with { Depth = 0 },
            [.. reached.Select(ci => ci with { Depth = depthById[ci.CiId] })],
            tickets.Tickets,
            tickets.Total,
            depth,
            MaxDepthReached: depthById.Values.Any(reachedAt => reachedAt == depth),
            ContainsCycle: CiGraphAnalyzer.ContainsCycle(edges)));
    }

    /// <summary>
    /// CIs in the shape the analyzer reads them. The discriminator is read with
    /// <c>EF.Property</c> because <see cref="ConfigurationItem.Type"/> is <c>builder.Ignore</c>d and a
    /// projection of it compiles and then fails at runtime as a 500 — WP-4.3's trap, still live.
    /// </summary>
    private async Task<IReadOnlyList<ImpactCi>> LoadAsync(
        System.Linq.Expressions.Expression<Func<ConfigurationItem, bool>> predicate,
        CancellationToken cancellationToken) =>
        await dbContext.Cis
            .AsNoTracking()
            .Where(predicate)
            .Select(ci => new ImpactCi(
                ci.Id,
                ci.Name,
                EF.Property<CiType>(ci, "CiType"),
                ci.LifecycleState,
                ci.IsActive,
                0,
                ci.OwnerUserId,
                ci.OwnerName,
                ci.DepartmentId,
                ci.DepartmentName,
                ci.SiteName))
            .ToListAsync(cancellationToken);
}
