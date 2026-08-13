namespace Modules.Monitoring.Data;

/// <summary>
/// Where to look for devices nobody has told the platform about, and how hard to look.
/// <para>
/// The discovery counterpart of <see cref="MonitoredDevice"/>, and deliberately not a device: a
/// monitored device names one CI that already exists, while a scan profile names an address range
/// whose contents are unknown — that is the whole point of scanning it. Nothing here references the
/// CMDB, because a scan runs before anything is known well enough to reference.
/// </para>
/// <para>
/// Profiles are owned by a <see cref="DiscoveryGroup"/> the same way devices are owned by a poller
/// group: it is the only thing that decides which scanner runs which range, and the two meet on the
/// string and nothing else.
/// </para>
/// </summary>
public sealed class ScanProfile
{
    public Guid Id { get; set; }

    /// <summary>Unique, and how an operator refers to the scan in a log line or a review queue.</summary>
    public required string Name { get; set; }

    public string? Description { get; set; }

    /// <summary>Which discovery service runs this profile. Matched exactly by the config fetch.</summary>
    public required string DiscoveryGroup { get; set; }

    /// <summary>
    /// What to scan, as a jsonb array of strings: a CIDR block (<c>10.0.0.0/24</c>), a single address,
    /// an inclusive last-octet range (<c>10.0.0.5-40</c>), or the keyword <c>local</c> meaning the
    /// subnet the scanner itself sits on.
    /// <para>
    /// Held as a string list rather than as rows for the reason WP-3.1 held check parameters as jsonb:
    /// the shapes a range can take belong to the scanner, and a table of them would need a migration
    /// every time a new form is accepted. <c>ScanProfileRules</c> validates each string at the edge,
    /// and expanding one into addresses is the discovery service's job — this side never enumerates.
    /// </para>
    /// </summary>
    public required string RangesJson { get; set; }

    /// <summary>
    /// TCP ports to fingerprint on every address that answers, as a jsonb array of numbers. Empty is
    /// legal and means an ICMP-only sweep, which is the cheapest useful scan there is.
    /// </summary>
    public required string PortsJson { get; set; }

    /// <summary>How long between runs of this profile. Minutes, because a subnet sweep is not a poll.</summary>
    public int IntervalMinutes { get; set; }

    /// <summary>Seconds one probe against one address may take — the ping, the connect, the SNMP get.</summary>
    public int TimeoutSeconds { get; set; }

    /// <summary>
    /// Whether to ask each responding address to identify itself over SNMP. The communities tried are
    /// the scanner's own configuration and are never stored here — a profile is scan policy, not a
    /// credential, and WP-3.11 is the only place in this platform a secret lives.
    /// </summary>
    public bool SnmpEnabled { get; set; } = true;

    /// <summary>
    /// Whether to walk LLDP and CDP on devices that answered SNMP. Separate from
    /// <see cref="SnmpEnabled"/> because the neighbour tables are the expensive part of an identify —
    /// two walks per device — and an estate of servers has nothing to report in them.
    /// </summary>
    public bool NeighbourDiscoveryEnabled { get; set; } = true;

    /// <summary>A disabled profile stays configured and leaves every scanner's configuration.</summary>
    public bool IsEnabled { get; set; } = true;

    public required string CreatedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public required string UpdatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
