using Modules.Assets.Data;

namespace Modules.Assets.Features.Topology;

/// <summary>
/// What to draw. The map is the whole estate rather than a walk from one CI: a topology map answers
/// "what is out there and how is it wired", which is not a question with a root.
/// </summary>
/// <param name="Types">
/// Restrict to these CI types, empty for all. A network map is the common case and drawing every
/// piece of installed software beside the switches buries it.
/// </param>
/// <param name="IncludeIsolated">
/// Whether to include CIs no edge touches. Off by default — a field of disconnected dots is not a
/// topology, and the CI list is the screen for "everything we own".
/// </param>
public sealed record TopologyRequest(
    IReadOnlyList<CiType>? Types = null,
    bool IncludeIsolated = false);

/// <summary>
/// One CI on the map. Carries no status: a node is coloured from the monitoring status board, which
/// the browser reads separately and joins on <see cref="CiId"/>. Assets does not query the monitoring
/// schema (ARCHITECTURE §3), and a CI nothing monitors legitimately has no status at all.
/// </summary>
/// <param name="Address">
/// The management IP a network CI records, else the address a scan last found it on. Shown under the
/// name so two identically-named nodes can be told apart.
/// </param>
/// <param name="LastSeenByDiscoveryAt">
/// When a scan last had this CI answer, or null if none ever has. The difference between "the CMDB
/// says this exists" and "the network agrees" — and the one fact on the node that a scan wrote.
/// </param>
public sealed record TopologyNode(
    Guid CiId,
    string Name,
    CiType Type,
    CiLifecycleState LifecycleState,
    bool IsActive,
    string? SiteName,
    string? Address,
    DateTimeOffset? LastSeenByDiscoveryAt,
    /// <summary>
    /// Edge, Firewall, Core, Distribution, Access or Wireless for a network device; null for every
    /// other CI type and for a device nobody has given a role yet. This is what lets the map put a
    /// core switch above an access switch without reading either one's name.
    /// </summary>
    string? NetworkRole = null);

/// <summary>
/// One relationship somebody asserted, as the map draws it.
/// </summary>
/// <param name="ObservedByDiscovery">
/// True when a scan also saw these two CIs as neighbours of each other. The edge is still the
/// operator's assertion — this only says the network agreed with it — and it is what lets the map
/// distinguish a link that is real from one that is merely recorded.
/// </param>
public sealed record TopologyEdge(
    Guid Id,
    Guid SourceCiId,
    Guid TargetCiId,
    CiRelationshipType Type,
    string? Description,
    bool ObservedByDiscovery);

/// <summary>
/// One link a scan observed through LLDP or CDP, with both ends resolved to CIs.
/// <para>
/// This is emphatically <b>not</b> a relationship and nothing writes it to
/// <c>assets.ci_relationships</c>. A scan observes; an operator asserts. Turning an observation into a
/// stored edge would make the two indistinguishable a week later and destroy exactly the difference
/// WP-4.6's drift report exists to find — the same call WP-4.2 made when it refused to let a match
/// overwrite a CI's own attributes.
/// </para>
/// </summary>
/// <param name="Id">
/// Synthesised from the pair, because there is no row behind it: <c>observed:{lower}:{higher}</c>.
/// Stable across requests so React Flow can keep the edge identity between renders.
/// </param>
/// <param name="SourceCiId">
/// The lower of the two CI ids. A link is symmetric — the reporting device is an accident of which
/// end runs an SNMP agent — so the ends are ordered by id rather than by who spoke first, which is
/// what makes two devices reporting each other collapse into one link instead of two.
/// </param>
/// <param name="ConfirmedByBothEnds">
/// True when each CI reported the other. One-sided is the normal case (only one end is scanned, or
/// only one runs LLDP) and is not a weaker link, but a two-sided report is the strongest evidence a
/// scan can produce and the map says so.
/// </param>
/// <param name="MatchesAssertedEdge">
/// True when a relationship already joins these two CIs in either direction. The map draws one line
/// per pair: an observed link that matches is folded into the asserted edge as confirmation, and one
/// that does not is drawn dashed as something the CMDB has not recorded yet.
/// </param>
public sealed record TopologyObservedLink(
    string Id,
    Guid SourceCiId,
    Guid TargetCiId,
    IReadOnlyList<string> Protocols,
    string? SourcePort,
    string? TargetPort,
    bool ConfirmedByBothEnds,
    bool MatchesAssertedEdge);

/// <summary>
/// A neighbour a device reported that no CI could be found for — the far end of a link into
/// something the CMDB does not know about, or knows about under two names it cannot choose between.
/// <para>
/// Deliberately not drawn as a node. A node on the topology map is a CI; inventing one for an
/// unresolved report would put something on the map that nothing else in the platform can open, and
/// the place where an unknown device becomes a CI is WP-4.2's review queue. The map counts them and
/// points there instead.
/// </para>
/// </summary>
public sealed record TopologyUnresolvedNeighbour(
    Guid ReportedByCiId,
    string ReportedByCiName,
    string Protocol,
    string? LocalPort,
    string? RemoteSystemName,
    string? RemotePort,
    string? RemoteAddress,
    TopologyResolutionFailure Reason);

/// <summary>Why a neighbour report could not be placed against a CI.</summary>
public enum TopologyResolutionFailure
{
    /// <summary>Nothing in the CMDB is named that or records that address.</summary>
    NoCandidate = 1,

    /// <summary>
    /// A rung found more than one CI. Not resolved to either, for the reason WP-4.2 gives for an
    /// ambiguous match: picking one silently is how a map fills with wrong lines.
    /// </summary>
    Ambiguous = 2,

    /// <summary>The report named nothing at all — no remote name and no remote address.</summary>
    NoIdentity = 3,
}

/// <param name="NodeLimitReached">
/// True when the estate has more CIs than the map will draw. The nodes returned are the ones with the
/// most edges, so the core of the estate survives the cut; a truncated picture must never look like a
/// complete one, which is the flag WP-2.4 already established for the CI page's mini-graph.
/// </param>
public sealed record TopologyResponse(
    IReadOnlyList<TopologyNode> Nodes,
    IReadOnlyList<TopologyEdge> Edges,
    IReadOnlyList<TopologyObservedLink> ObservedLinks,
    IReadOnlyList<TopologyUnresolvedNeighbour> UnresolvedNeighbours,
    int NodeLimit,
    bool NodeLimitReached);

/// <summary>One saved layout, without its positions — what the map picker lists.</summary>
public sealed record TopologyMapSummaryResponse(
    Guid Id,
    string Name,
    string? Description,
    int PinnedNodeCount,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    string? UpdatedBy,
    DateTimeOffset UpdatedAt);

public sealed record TopologyMapNodeResponse(Guid CiId, double X, double Y);

public sealed record TopologyMapResponse(
    Guid Id,
    string Name,
    string? Description,
    IReadOnlyList<TopologyMapNodeResponse> Nodes,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    string? UpdatedBy,
    DateTimeOffset UpdatedAt);

/// <param name="Nodes">
/// Every node the operator has pinned, and only those. A CI absent from this list is not hidden —
/// it falls back to auto-layout — so saving a map does not freeze the estate as it looked that day,
/// and a switch racked tomorrow appears on every saved map rather than on none of them.
/// </param>
public sealed record SaveTopologyMapRequest(
    string Name,
    string? Description,
    IReadOnlyList<TopologyMapNodeResponse> Nodes);

public enum TopologyMapOutcome
{
    Success,
    NotFound,

    /// <summary>Another map already has this name.</summary>
    DuplicateName,

    /// <summary>A pinned position names a CI that does not exist.</summary>
    UnknownCi,
}

public sealed record TopologyMapResult(
    TopologyMapOutcome Outcome,
    TopologyMapResponse? Map = null,
    IReadOnlyDictionary<string, string[]>? Errors = null,
    string? Error = null);
