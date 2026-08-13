namespace Contracts.Events;

/// <summary>
/// One device a scan found on the network, and everything the scan learned about it.
/// <para>
/// One message per device rather than one per scan, unlike <see cref="DeviceTelemetryReported"/>:
/// telemetry is the same devices every fifteen seconds and batching is what keeps that off the bus,
/// while a scan runs on a schedule measured in minutes and reports only the addresses that answered.
/// The consumer's unit of work is also one device — WP-4.2 matches each against the CMDB and queues
/// what it cannot place — so a batch would be unpacked on arrival and re-keyed for dedupe.
/// </para>
/// <para>
/// This states what was seen, never what it means. Whether the address is already a CI, whether it
/// should become one, and whether anybody has to approve that are all WP-4.2's questions.
/// </para>
/// </summary>
/// <param name="ScanId">
/// The scan run this came from. One id shared by every device a single pass of one profile found, so
/// a consumer can tell "the estate as this scan saw it" from two scans interleaved.
/// </param>
/// <param name="Address">The IPv4 address that answered. The one field always present.</param>
/// <param name="Hostname">Reverse DNS, where the resolver answered; null otherwise.</param>
/// <param name="RespondedToPing">
/// False for a device found only because a fingerprinted port was open — a host that drops ICMP is
/// still a host, and treating silence as absence is how half an estate goes missing.
/// </param>
/// <param name="OpenPorts">Which of the profile's fingerprint ports accepted a connection.</param>
/// <param name="Snmp">What the agent said about itself, or null when SNMP did not answer.</param>
/// <param name="Neighbours">LLDP or CDP neighbours the device reported; empty when it reported none.</param>
public sealed record DeviceDiscovered(
    Guid EventId,
    DateTimeOffset OccurredAt,
    string DiscoveryName,
    Guid ScanProfileId,
    string ScanProfileName,
    Guid ScanId,
    string Address,
    string? Hostname,
    bool RespondedToPing,
    IReadOnlyList<int> OpenPorts,
    DiscoveredSnmpIdentity? Snmp,
    IReadOnlyList<DiscoveredNeighbour> Neighbours);

/// <summary>
/// A device's own description of itself, read from SNMPv2-MIB::system.
/// <para>
/// Every field is optional because every field genuinely is: an agent that answers sysDescr and
/// nothing else is common, and refusing the identity because sysLocation was never configured would
/// discard the one fact that tells a router from a printer.
/// </para>
/// </summary>
/// <para>
/// It deliberately does not say which community string the agent answered on. That is the one thing
/// the scan learns that is a secret in a real estate, and this event travels the bus and lands in a
/// review queue somebody reads — the vault exists precisely so that secrets are not fields of events
/// (ARCHITECTURE §7.3). The scanner logs the <em>position</em> of the community that worked in its own
/// configured list, which identifies it without printing it, following the rule WP-3.11 set for the
/// poller's credential logging. Attaching a vault credential to a discovered device is part of
/// approving it, which is WP-4.2's.
/// </para>
public sealed record DiscoveredSnmpIdentity(
    string? SysName,
    string? SysDescription,
    string? SysObjectId,
    string? SysLocation,
    string? SysContact,
    double? UptimeSeconds);

/// <summary>
/// One neighbour a device reported, from LLDP-MIB or Cisco's CDP.
/// <para>
/// Carried as the device said it, with no attempt to resolve either end to a CI. Two devices that
/// both report the link produce two of these from opposite sides, and reconciling them into one edge
/// is WP-4.3's topology work rather than the scanner's.
/// </para>
/// </summary>
/// <param name="Protocol">Either <c>lldp</c> or <c>cdp</c>; a device may report both.</param>
/// <param name="LocalPort">The reporting device's own interface, as it names it.</param>
/// <param name="RemoteSystemName">The neighbour's hostname, where it advertised one.</param>
/// <param name="RemotePort">The neighbour's interface at the far end of the link.</param>
/// <param name="RemoteAddress">The neighbour's management address, where it advertised one.</param>
public sealed record DiscoveredNeighbour(
    string Protocol,
    string? LocalPort,
    string? RemoteSystemName,
    string? RemotePort,
    string? RemoteAddress);
