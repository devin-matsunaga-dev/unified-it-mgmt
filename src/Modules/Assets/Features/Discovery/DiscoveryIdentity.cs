using Contracts.Events;

namespace Modules.Assets.Features.Discovery;

/// <summary>
/// The three facts that can identify a discovered device, normalised once so that every comparison in
/// this feature is made on the same shapes.
/// </summary>
/// <param name="Address">Always present — the address that answered.</param>
/// <param name="Hostname">The short reverse-DNS name, lowercased, domain stripped.</param>
/// <param name="SysName">What the device calls itself over SNMP, lowercased.</param>
public sealed record DiscoveryFingerprint(string Address, string? Hostname, string? SysName)
{
    /// <summary>
    /// The names this device might be recorded under, strongest first and without duplicates. Both
    /// rungs that match on a name walk this list, so "sysName wins over reverse DNS" is stated once.
    /// </summary>
    public IReadOnlyList<string> Names =>
        SysName is null
            ? Hostname is null ? [] : [Hostname]
            : Hostname is null || string.Equals(Hostname, SysName, StringComparison.Ordinal)
                ? [SysName]
                : [SysName, Hostname];
}

/// <summary>
/// How a discovery is turned into a stable identity, and the one place the normalisation rules live.
/// Pure: no database, no clock, no configuration.
/// </summary>
public static class DiscoveryIdentity
{
    public static DiscoveryFingerprint FingerprintOf(DeviceDiscovered discovery)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        return new DiscoveryFingerprint(
            discovery.Address.Trim(),
            ShortHostname(discovery.Hostname),
            Normalise(discovery.Snmp?.SysName));
    }

    /// <summary>
    /// The key a ledger row is filed under, most stable first.
    /// <para>
    /// A device's own name for itself outranks reverse DNS, which outranks the address, because that is
    /// the order in which they survive the estate changing around them: DHCP moves an address, a DNS
    /// zone edit moves a hostname, and sysName moves when somebody reconfigures the device. The tiers
    /// are not stable across a device gaining an SNMP agent, which is exactly why the intake looks a
    /// row up by <em>every</em> field rather than by this key alone, and rewrites the key in place when
    /// it finds a better one.
    /// </para>
    /// </summary>
    public static string KeyFor(DiscoveryFingerprint fingerprint)
    {
        ArgumentNullException.ThrowIfNull(fingerprint);
        return fingerprint.SysName is { } sysName
            ? $"snmp:{sysName}"
            : fingerprint.Hostname is { } hostname
                ? $"host:{hostname}"
                : $"addr:{fingerprint.Address}";
    }

    /// <summary>
    /// The leftmost label of a fully qualified name, lowercased —
    /// <c>sim-switch-healthy.example.test</c> becomes <c>sim-switch-healthy</c>.
    /// <para>
    /// A CI records the short name an operator typed while a resolver answers with a domain attached,
    /// so comparing the two unshortened matches nothing. An address that arrived in the hostname field
    /// is refused rather than truncated: <c>172</c> is not a hostname, and treating it as one would make
    /// every device in a /8 share a name.
    /// </para>
    /// </summary>
    public static string? ShortHostname(string? hostname)
    {
        var normalised = Normalise(hostname);
        if (normalised is null || System.Net.IPAddress.TryParse(normalised, out _))
        {
            return null;
        }

        var label = normalised.Split('.', 2)[0];
        return label.Length == 0 ? null : label;
    }

    /// <summary>Trimmed and lowercased, or null when there is nothing to compare.</summary>
    public static string? Normalise(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();
}
