using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

using Modules.Assets.Data;
using Modules.Assets.Features.Discovery;

namespace Modules.Assets.Features.Topology;

/// <summary>
/// Assembles the topology map from the two things the platform already knows: the relationships
/// WP-2.3 stores and the LLDP/CDP neighbours WP-4.1 collects and WP-4.2 files against a CI.
/// <para>
/// It writes nothing. A scan's observation reaches the map as an <see cref="TopologyObservedLink"/>
/// and never as a row in <c>assets.ci_relationships</c> — see that type's own remarks for why.
/// </para>
/// </summary>
public sealed class TopologyService(AssetsDbContext dbContext, IConfiguration configuration) : ITopologyService
{
    /// <summary>
    /// The most nodes the map will draw, unless <c>Assets:Topology:NodeLimit</c> says otherwise.
    /// <para>
    /// React Flow renders a few hundred nodes comfortably and a few thousand not at all, and a picture
    /// nobody can read is worse than a truncated one that says so. It is configuration rather than a
    /// constant because the right number is a property of the deployment's estate and its operators'
    /// screens, not of this code — and because a rendering budget is exactly the sort of thing that has
    /// to be movable without a release.
    /// </para>
    /// </summary>
    public const int DefaultNodeLimit = 400;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private int NodeLimit =>
        configuration.GetValue<int?>("Assets:Topology:NodeLimit") is { } configured and > 0
            ? configured
            : DefaultNodeLimit;

    public async Task<TopologyResponse> GetAsync(TopologyRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var relationships = await dbContext.CiRelationships.AsNoTracking()
            .Select(relationship => new RelationshipRow(
                relationship.Id,
                relationship.SourceCiId,
                relationship.TargetCiId,
                relationship.Type,
                relationship.Description))
            .ToListAsync(cancellationToken);

        var assertedPairs = relationships
            .Select(relationship => TopologyNeighbourReconciler.Pair(
                relationship.SourceCiId, relationship.TargetCiId))
            .ToHashSet();

        var facts = await dbContext.CiDiscoveryFacts.AsNoTracking()
            .Select(row => new FactsRow(row.CiId, row.Address, row.SysName, row.NeighboursJson, row.LastSeenAt))
            .ToListAsync(cancellationToken);

        var reconciliation = await ReconcileAsync(facts, assertedPairs, cancellationToken);

        // Everything an edge or an observed link touches. Isolated CIs are only fetched when asked for,
        // so the usual request costs one query over the connected estate rather than over the whole
        // CMDB — a map of a thousand unconnected laptops is a list, not a topology.
        var touched = relationships
            .SelectMany(relationship => new[] { relationship.SourceCiId, relationship.TargetCiId })
            .Concat(reconciliation.Links.SelectMany(link => new[] { link.SourceCiId, link.TargetCiId }))
            .Distinct()
            .ToList();

        var types = request.Types is { Count: > 0 } ? request.Types.Distinct().ToArray() : null;
        var ciQuery = dbContext.Cis.AsNoTracking();
        if (!request.IncludeIsolated)
        {
            ciQuery = ciQuery.Where(ci => touched.Contains(ci.Id));
        }

        if (types is not null)
        {
            // The CI type is the TPH discriminator and `ConfigurationItem.Type` is explicitly ignored
            // by the model, so it cannot be filtered or projected directly. `OfType<T>()` is the shape
            // the CI list uses, but it takes one type at a time and this filter takes a set; the shadow
            // property is the same column, read once, for any number of them.
            ciQuery = ciQuery.Where(ci => types.Contains(EF.Property<CiType>(ci, "CiType")));
        }

        var cis = await ciQuery
            .Select(ci => new CiRow(
                ci.Id,
                ci.Name,
                EF.Property<CiType>(ci, "CiType"),
                ci.LifecycleState,
                ci.IsActive,
                ci.SiteName,
                ci is NetworkDeviceCi ? ((NetworkDeviceCi)ci).ManagementIp : null))
            .ToListAsync(cancellationToken);

        // The type filter is applied to the nodes and the edges follow, rather than the other way
        // round: an edge with one end filtered off the map has nowhere to land.
        var byId = cis.ToDictionary(ci => ci.Id);
        var edges = relationships
            .Where(relationship => byId.ContainsKey(relationship.SourceCiId)
                && byId.ContainsKey(relationship.TargetCiId))
            .ToList();
        var links = reconciliation.Links
            .Where(link => byId.ContainsKey(link.SourceCiId) && byId.ContainsKey(link.TargetCiId))
            .ToList();

        var limit = NodeLimit;
        var kept = SelectNodes(cis, edges, links, limit, out var limitReached);
        var keptIds = kept.Select(ci => ci.Id).ToHashSet();
        edges = [.. edges.Where(edge => keptIds.Contains(edge.SourceCiId) && keptIds.Contains(edge.TargetCiId))];
        links = [.. links.Where(link => keptIds.Contains(link.SourceCiId) && keptIds.Contains(link.TargetCiId))];

        var observedPairs = links
            .Select(link => TopologyNeighbourReconciler.Pair(link.SourceCiId, link.TargetCiId))
            .ToHashSet();
        var lastSeenByCi = facts.ToDictionary(row => row.CiId, row => row.LastSeenAt);
        var addressByCi = facts.ToDictionary(row => row.CiId, row => row.Address);

        return new TopologyResponse(
            [.. kept
                .Select(ci => new TopologyNode(
                    ci.Id,
                    ci.Name,
                    ci.Type,
                    ci.LifecycleState,
                    ci.IsActive,
                    ci.SiteName,
                    // What the CMDB records for the device beats what a scan happened to find it on:
                    // the address on the node is the one an operator would type into a browser.
                    string.IsNullOrWhiteSpace(ci.ManagementIp)
                        ? addressByCi.GetValueOrDefault(ci.Id)
                        : ci.ManagementIp,
                    lastSeenByCi.TryGetValue(ci.Id, out var lastSeen) ? lastSeen : null))
                .OrderBy(node => node.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(node => node.CiId)],
            [.. edges
                .Select(edge => new TopologyEdge(
                    edge.Id,
                    edge.SourceCiId,
                    edge.TargetCiId,
                    edge.Type,
                    edge.Description,
                    observedPairs.Contains(TopologyNeighbourReconciler.Pair(edge.SourceCiId, edge.TargetCiId))))
                .OrderBy(edge => edge.Id)],
            [.. links],
            // An unresolved report about a CI that was filtered off the map is not a finding, so the
            // list is narrowed to the reporters still on it.
            [.. reconciliation.Unresolved.Where(neighbour => keptIds.Contains(neighbour.ReportedByCiId))],
            limit,
            limitReached);
    }

    private async Task<TopologyReconciliation> ReconcileAsync(
        IReadOnlyList<FactsRow> facts,
        IReadOnlySet<(Guid, Guid)> assertedPairs,
        CancellationToken cancellationToken)
    {
        var reporterIds = facts.Select(row => row.CiId).ToList();
        var reporterNames = await dbContext.Cis.AsNoTracking()
            .Where(ci => reporterIds.Contains(ci.Id))
            .Select(ci => new { ci.Id, ci.Name })
            .ToDictionaryAsync(ci => ci.Id, ci => ci.Name, cancellationToken);

        var reports = new List<ObservedNeighbourReport>();
        foreach (var row in facts)
        {
            if (!reporterNames.TryGetValue(row.CiId, out var reporterName))
            {
                continue;
            }

            foreach (var neighbour in Deserialise(row.NeighboursJson))
            {
                reports.Add(new ObservedNeighbourReport(
                    row.CiId,
                    reporterName,
                    neighbour.Protocol,
                    neighbour.LocalPort,
                    neighbour.RemoteSystemName,
                    neighbour.RemotePort,
                    neighbour.RemoteAddress));
            }
        }

        if (reports.Count == 0)
        {
            return new TopologyReconciliation([], []);
        }

        var identities = await LoadIdentitiesAsync(reports, facts, cancellationToken);
        return TopologyNeighbourReconciler.Reconcile(reports, identities, assertedPairs);
    }

    /// <summary>
    /// The CIs a neighbour report could name, fetched by exact equality on the names and addresses the
    /// reports actually mention — the same shape WP-4.2's candidate load uses, and for the same reason:
    /// the cost follows the number of neighbour reports rather than the size of the estate.
    /// <para>
    /// The two discovery rungs need no query at all. A CI's observed address and sysName are already in
    /// the facts rows this pass loaded to find the neighbours in the first place.
    /// </para>
    /// </summary>
    private async Task<IReadOnlyList<TopologyCiIdentity>> LoadIdentitiesAsync(
        IReadOnlyList<ObservedNeighbourReport> reports,
        IReadOnlyList<FactsRow> facts,
        CancellationToken cancellationToken)
    {
        var identities = facts
            .Select(row => new TopologyCiIdentity(
                row.CiId,
                Name: null,
                SysName: row.SysName,
                DiscoveredAddress: row.Address))
            .ToList();

        var addresses = reports
            .Select(report => DiscoveryIdentity.Normalise(report.RemoteAddress))
            .Where(address => address is not null)
            .Distinct()
            .ToArray();
        var names = reports
            .SelectMany(report => new[]
            {
                DiscoveryIdentity.Normalise(report.RemoteSystemName),
                DiscoveryIdentity.ShortHostname(report.RemoteSystemName),
            })
            .Where(name => name is not null)
            .Distinct()
            .ToArray();

        if (addresses.Length > 0)
        {
            identities.AddRange(await dbContext.Cis.AsNoTracking().OfType<NetworkDeviceCi>()
                .Where(ci => addresses.Contains(ci.ManagementIp.ToLower()))
                .Select(ci => new TopologyCiIdentity(ci.Id, ci.Name, null, ci.ManagementIp, null, null))
                .ToListAsync(cancellationToken));
        }

        if (names.Length > 0)
        {
            identities.AddRange(await dbContext.Cis.AsNoTracking().OfType<ServerCi>()
                .Where(ci => names.Contains(ci.Hostname.ToLower()))
                .Select(ci => new TopologyCiIdentity(ci.Id, ci.Name, ci.Hostname, null, null, null))
                .ToListAsync(cancellationToken));
            identities.AddRange(await dbContext.Cis.AsNoTracking().OfType<VirtualCi>()
                .Where(ci => names.Contains(ci.Hostname.ToLower()))
                .Select(ci => new TopologyCiIdentity(ci.Id, ci.Name, ci.Hostname, null, null, null))
                .ToListAsync(cancellationToken));
            identities.AddRange(await dbContext.Cis.AsNoTracking()
                .Where(ci => names.Contains(ci.Name.ToLower()))
                .Select(ci => new TopologyCiIdentity(ci.Id, ci.Name, null, null, null, null))
                .ToListAsync(cancellationToken));
        }

        return identities;
    }

    /// <summary>
    /// The nodes that survive the limit, ranked by how many edges touch them. A cut by name or by id
    /// would take an arbitrary slice out of the middle of the estate; a cut by degree keeps the core
    /// switches and drops the leaves, which is the half of the picture worth seeing.
    /// </summary>
    private static List<CiRow> SelectNodes(
        List<CiRow> cis,
        List<RelationshipRow> edges,
        List<TopologyObservedLink> links,
        int limit,
        out bool limitReached)
    {
        var degree = cis.ToDictionary(ci => ci.Id, _ => 0);
        foreach (var end in edges
            .SelectMany(edge => new[] { edge.SourceCiId, edge.TargetCiId })
            .Concat(links.SelectMany(link => new[] { link.SourceCiId, link.TargetCiId })))
        {
            if (degree.ContainsKey(end))
            {
                degree[end]++;
            }
        }

        limitReached = cis.Count > limit;
        return limitReached
            ? [.. cis
                .OrderByDescending(ci => degree[ci.Id])
                .ThenBy(ci => ci.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(ci => ci.Id)
                .Take(limit)]
            : cis;
    }

    private static IReadOnlyList<DiscoveredNeighbourResponse> Deserialise(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<DiscoveredNeighbourResponse>>(json, Json) ?? [];
        }
        catch (JsonException)
        {
            // A facts row whose neighbours will not parse is one device's cabling, not the map. The
            // same swallow WP-4.2's own reader makes, for the same reason.
            return [];
        }
    }

    private sealed record RelationshipRow(
        Guid Id,
        Guid SourceCiId,
        Guid TargetCiId,
        CiRelationshipType Type,
        string? Description);

    private sealed record FactsRow(
        Guid CiId,
        string Address,
        string? SysName,
        string NeighboursJson,
        DateTimeOffset LastSeenAt);

    private sealed record CiRow(
        Guid Id,
        string Name,
        CiType Type,
        CiLifecycleState LifecycleState,
        bool IsActive,
        string? SiteName,
        string? ManagementIp);
}
