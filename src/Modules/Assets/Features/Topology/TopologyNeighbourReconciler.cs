using Modules.Assets.Features.Discovery;

namespace Modules.Assets.Features.Topology;

/// <summary>
/// One neighbour report as a device sent it, tagged with the CI that reported it. The reporting end
/// is always known — a report only exists because it arrived attached to a CI's discovery facts —
/// and it is the far end this feature has to resolve.
/// </summary>
public sealed record ObservedNeighbourReport(
    Guid ReportedByCiId,
    string ReportedByCiName,
    string Protocol,
    string? LocalPort,
    string? RemoteSystemName,
    string? RemotePort,
    string? RemoteAddress);

/// <summary>
/// Everything a neighbour report could name a CI by, normalised by the service before it gets here.
/// <para>
/// Deliberately not <c>CiMatchCandidate</c>, which WP-4.2's matcher uses. That one answers "is this
/// discovery this CI" from an address and a hostname; this one has to answer "which CI is the thing
/// at the far end of this cable", and the strongest signal for that is what a scan already heard the
/// CI call itself (<see cref="SysName"/>) — a field the discovery matcher has no use for, because on
/// its side of the problem the sysName is what is being matched rather than what is being matched
/// against.
/// </para>
/// </summary>
/// <param name="Name">
/// Null on a partial identity — a row that carries only some of the rungs, which is how the service
/// contributes what discovery observed about a CI without re-querying the CI itself. Several rows may
/// speak for one CI; they are read rung by rung, never merged.
/// </param>
public sealed record TopologyCiIdentity(
    Guid CiId,
    string? Name,
    string? Hostname = null,
    string? ManagementIp = null,
    string? SysName = null,
    string? DiscoveredAddress = null);

public sealed record TopologyReconciliation(
    IReadOnlyList<TopologyObservedLink> Links,
    IReadOnlyList<TopologyUnresolvedNeighbour> Unresolved);

/// <summary>
/// Turns a pile of one-sided LLDP and CDP reports into links between CIs — the reconciliation
/// <see cref="Contracts.Events.DiscoveredNeighbour"/>'s own documentation defers to this package.
/// <para>
/// Two things happen here. Each report's far end is resolved to a CI by walking the same
/// strongest-first ladder WP-4.2 established, stopping at the first rung that finds anything — a rung
/// that finds two CIs does not fall through to a weaker one, because "and one of them is also named
/// after it" picks a winner without resolving anything. Then the resolved reports are folded by
/// unordered pair, so a switch and a router each reporting the other become one link rather than two
/// opposing arrows.
/// </para>
/// <para>
/// Pure: no database, no clock, no configuration. The whole matrix is unit-tested.
/// </para>
/// </summary>
public static class TopologyNeighbourReconciler
{
    /// <param name="assertedPairs">
    /// The unordered CI pairs that already have a relationship between them, so each link can say
    /// whether it confirms one. Order within a pair is ignored: an operator recording "switch connects
    /// to router" and a scan seeing the router's side of the same cable are the same link.
    /// </param>
    public static TopologyReconciliation Reconcile(
        IReadOnlyList<ObservedNeighbourReport> reports,
        IReadOnlyList<TopologyCiIdentity> identities,
        IReadOnlySet<(Guid, Guid)> assertedPairs)
    {
        ArgumentNullException.ThrowIfNull(reports);
        ArgumentNullException.ThrowIfNull(identities);
        ArgumentNullException.ThrowIfNull(assertedPairs);

        var rungs = BuildRungs(identities);
        var links = new Dictionary<(Guid, Guid), LinkBuilder>();
        var unresolved = new List<TopologyUnresolvedNeighbour>();

        foreach (var report in reports)
        {
            var resolution = Resolve(report, rungs);
            if (resolution.CiId is not { } remoteCiId)
            {
                unresolved.Add(new TopologyUnresolvedNeighbour(
                    report.ReportedByCiId,
                    report.ReportedByCiName,
                    report.Protocol,
                    report.LocalPort,
                    report.RemoteSystemName,
                    report.RemotePort,
                    report.RemoteAddress,
                    resolution.Reason));
                continue;
            }

            // A device reporting itself is a loop, not a link: a stacked switch advertising its own
            // sysName out of every member port would otherwise draw a circle on every node it touches.
            // Dropped rather than listed as unresolved — it resolved perfectly well, to nothing worth
            // drawing.
            if (remoteCiId == report.ReportedByCiId)
            {
                continue;
            }

            var pair = Pair(report.ReportedByCiId, remoteCiId);
            if (!links.TryGetValue(pair, out var link))
            {
                link = new LinkBuilder();
                links[pair] = link;
            }

            link.Add(report, reportedByLowEnd: report.ReportedByCiId == pair.Item1);
        }

        return new TopologyReconciliation(
            [.. links
                .Select(entry => entry.Value.Build(entry.Key, assertedPairs.Contains(entry.Key)))
                .OrderBy(link => link.Id, StringComparer.Ordinal)],
            [.. unresolved
                .OrderBy(neighbour => neighbour.ReportedByCiName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(neighbour => neighbour.LocalPort, StringComparer.OrdinalIgnoreCase)
                .ThenBy(neighbour => neighbour.RemoteSystemName, StringComparer.OrdinalIgnoreCase)]);
    }

    /// <summary>The two ids of a link, lowest first, so one cable has one key however it was reported.</summary>
    public static (Guid, Guid) Pair(Guid first, Guid second) =>
        first.CompareTo(second) <= 0 ? (first, second) : (second, first);

    private sealed record Resolution(Guid? CiId, TopologyResolutionFailure Reason);

    /// <summary>
    /// The ladder, strongest first. An address a device advertises as its management address is a
    /// stronger claim than a name, because names are typed by people and addresses are configured on
    /// the thing itself; and among the names, what the device calls itself over SNMP outranks what a
    /// resolver says about it, which outranks what somebody wrote on the CI — the same ordering
    /// WP-4.2's ledger uses for exactly the same reason.
    /// </summary>
    private sealed record Rungs(
        ILookup<string, Guid> ManagementIp,
        ILookup<string, Guid> DiscoveredAddress,
        ILookup<string, Guid> SysName,
        ILookup<string, Guid> Hostname,
        ILookup<string, Guid> Name);

    private static Rungs BuildRungs(IReadOnlyList<TopologyCiIdentity> identities) => new(
        Index(identities, identity => identity.ManagementIp),
        Index(identities, identity => identity.DiscoveredAddress),
        Index(identities, identity => identity.SysName),
        Index(identities, identity => identity.Hostname),
        Index(identities, identity => identity.Name));

    private static ILookup<string, Guid> Index(
        IReadOnlyList<TopologyCiIdentity> identities,
        Func<TopologyCiIdentity, string?> field) =>
        identities
            .Select(identity => (Key: DiscoveryIdentity.Normalise(field(identity)), identity.CiId))
            .Where(entry => entry.Key is not null)
            .ToLookup(entry => entry.Key!, entry => entry.CiId, StringComparer.Ordinal);

    private static Resolution Resolve(ObservedNeighbourReport report, Rungs rungs)
    {
        var address = DiscoveryIdentity.Normalise(report.RemoteAddress);
        var names = NamesOf(report.RemoteSystemName);
        if (address is null && names.Count == 0)
        {
            return new Resolution(null, TopologyResolutionFailure.NoIdentity);
        }

        if (address is not null)
        {
            if (Rung(rungs.ManagementIp, [address]) is { } byManagementIp) return byManagementIp;
            if (Rung(rungs.DiscoveredAddress, [address]) is { } byDiscovered) return byDiscovered;
        }

        if (names.Count > 0)
        {
            if (Rung(rungs.SysName, names) is { } bySysName) return bySysName;
            if (Rung(rungs.Hostname, names) is { } byHostname) return byHostname;
            if (Rung(rungs.Name, names) is { } byName) return byName;
        }

        return new Resolution(null, TopologyResolutionFailure.NoCandidate);
    }

    /// <summary>
    /// The forms a remote system name might be recorded under. LLDP carries whatever the far device
    /// was configured with, which is a fully qualified name about as often as it is a short one, while
    /// a CI records the short name somebody typed — so both are tried, longest first so an estate that
    /// genuinely records FQDNs is matched on the whole string rather than on its first label.
    /// </summary>
    private static IReadOnlyList<string> NamesOf(string? remoteSystemName)
    {
        var full = DiscoveryIdentity.Normalise(remoteSystemName);
        if (full is null)
        {
            return [];
        }

        var shortened = DiscoveryIdentity.ShortHostname(remoteSystemName);
        return shortened is null || string.Equals(shortened, full, StringComparison.Ordinal)
            ? [full]
            : [full, shortened];
    }

    /// <summary>
    /// One rung. Every key is tried before the rung answers, so a name that matches one CI in full and
    /// a different CI when shortened is an ambiguity rather than a race between two spellings.
    /// </summary>
    private static Resolution? Rung(ILookup<string, Guid> index, IReadOnlyList<string> keys)
    {
        var hits = keys.SelectMany(key => index[key]).Distinct().ToArray();
        return hits.Length switch
        {
            0 => null,
            1 => new Resolution(hits[0], TopologyResolutionFailure.NoCandidate),
            _ => new Resolution(null, TopologyResolutionFailure.Ambiguous),
        };
    }

    private sealed class LinkBuilder
    {
        private readonly SortedSet<string> _protocols = new(StringComparer.Ordinal);
        private string? _lowPort;
        private string? _highPort;
        private bool _lowReported;
        private bool _highReported;

        public void Add(ObservedNeighbourReport report, bool reportedByLowEnd)
        {
            if (DiscoveryIdentity.Normalise(report.Protocol) is { } protocol)
            {
                _protocols.Add(protocol);
            }

            // The reporter names its own interface directly and the far one only as the far device
            // advertised it, so a first-hand port always beats a second-hand one for the same end.
            if (reportedByLowEnd)
            {
                _lowReported = true;
                _lowPort = Trimmed(report.LocalPort) ?? _lowPort;
                _highPort ??= Trimmed(report.RemotePort);
            }
            else
            {
                _highReported = true;
                _highPort = Trimmed(report.LocalPort) ?? _highPort;
                _lowPort ??= Trimmed(report.RemotePort);
            }
        }

        public TopologyObservedLink Build((Guid, Guid) pair, bool matchesAssertedEdge) => new(
            $"observed:{pair.Item1}:{pair.Item2}",
            pair.Item1,
            pair.Item2,
            [.. _protocols],
            _lowPort,
            _highPort,
            _lowReported && _highReported,
            matchesAssertedEdge);

        private static string? Trimmed(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
