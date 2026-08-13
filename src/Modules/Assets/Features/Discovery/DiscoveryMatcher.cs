using Modules.Assets.Data;

namespace Modules.Assets.Features.Discovery;

/// <summary>
/// Enough of a CI to decide whether a discovery is it. Assembled by the service from three narrow
/// queries; the matcher itself never touches a database, so the whole ladder is unit-testable.
/// </summary>
/// <param name="ManagementIp">A network CI's recorded management address, else null.</param>
/// <param name="Hostname">A server or virtual CI's recorded hostname, else null.</param>
public sealed record CiMatchCandidate(
    Guid CiId,
    string Name,
    CiType Type,
    string? ManagementIp = null,
    string? Hostname = null);

/// <param name="Contenders">
/// Populated only for <see cref="DiscoveryMatchRule.Ambiguous"/>: the CIs that tied, so the review card
/// can name them and a human can settle it.
/// </param>
public sealed record DiscoveryMatch(
    Guid? CiId,
    DiscoveryMatchRule Rule,
    IReadOnlyList<Guid> Contenders)
{
    public static readonly DiscoveryMatch None = new(null, DiscoveryMatchRule.None, []);
}

/// <summary>
/// Which CI, if any, a discovery is — the heuristic half of WP-4.2.
/// <para>
/// <b>The WP text names MAC, serial and IP, and two of those do not exist here.</b> A sweep from
/// another subnet cannot see a MAC (ARP is link-local, and the ARP-table route is a walk of a
/// <em>router's</em> <c>ipNetToMediaPhysAddress</c> rather than a property of the scanned host), and
/// <see cref="Contracts.Events.DeviceDiscovered"/> carries no serial number because SNMP's system group
/// has no field for one. What a scan actually learns is an address, a reverse-DNS name and — when an
/// agent answers — the device's own name for itself, so those are what this matches on. WP-4.1's own
/// hand-verification recorded the same gap and told this package to plan for it.
/// </para>
/// <para>
/// The rungs are walked strongest first and the walk <em>stops</em> at the first rung that finds
/// anything, including when what it finds is two things. Falling through from an ambiguity to a weaker
/// rung would answer a question nobody asked: if two CIs both claim this management IP, the fact that
/// exactly one of them is also named after it does not resolve which is which, it just picks one.
/// </para>
/// </summary>
public static class DiscoveryMatcher
{
    /// <param name="monitoredCiId">
    /// The CI a monitored device already polls at this address, read through the Monitoring port. The
    /// strongest signal available and the only one that is not a heuristic at all: an operator created
    /// that device by hand and said this address is that CI.
    /// </param>
    public static DiscoveryMatch Match(
        DiscoveryFingerprint fingerprint,
        IReadOnlyList<CiMatchCandidate> candidates,
        Guid? monitoredCiId)
    {
        ArgumentNullException.ThrowIfNull(fingerprint);
        ArgumentNullException.ThrowIfNull(candidates);

        if (monitoredCiId is { } monitored)
        {
            return new DiscoveryMatch(monitored, DiscoveryMatchRule.MonitoredAddress, []);
        }

        if (Resolve(
                candidates.Where(candidate => Equal(candidate.ManagementIp, fingerprint.Address)),
                DiscoveryMatchRule.ManagementIp) is { } byAddress)
        {
            return byAddress;
        }

        var names = fingerprint.Names;
        if (names.Count == 0)
        {
            return DiscoveryMatch.None;
        }

        if (Resolve(
                candidates.Where(candidate => names.Any(name => Equal(candidate.Hostname, name))),
                DiscoveryMatchRule.Hostname) is { } byHostname)
        {
            return byHostname;
        }

        // The weakest rung that still fires, and it earns its place: an estate whose network CIs are
        // named after their hostnames is the normal case, and without this a scan of an estate that
        // records no management IPs matches nothing at all. It compares the CI's whole name, never a
        // prefix — "dc1-core-sw-01" must not match "dc1-core-sw-010".
        return Resolve(
                candidates.Where(candidate => names.Any(name => Equal(candidate.Name, name))),
                DiscoveryMatchRule.Name)
            ?? DiscoveryMatch.None;
    }

    /// <summary>
    /// One hit is a match, several are a question for a human, none falls through to the next rung.
    /// </summary>
    private static DiscoveryMatch? Resolve(IEnumerable<CiMatchCandidate> hits, DiscoveryMatchRule rule)
    {
        var ids = hits.Select(candidate => candidate.CiId).Distinct().ToArray();
        return ids.Length switch
        {
            0 => null,
            1 => new DiscoveryMatch(ids[0], rule, []),
            _ => new DiscoveryMatch(null, DiscoveryMatchRule.Ambiguous, ids),
        };
    }

    private static bool Equal(string? recorded, string? discovered) =>
        !string.IsNullOrWhiteSpace(recorded)
        && !string.IsNullOrWhiteSpace(discovered)
        && string.Equals(recorded.Trim(), discovered.Trim(), StringComparison.OrdinalIgnoreCase);
}
