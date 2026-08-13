namespace Modules.Assets.Data;

/// <summary>
/// One thing a scan found, and what the platform decided about it. The ledger behind WP-4.2's review
/// queue.
/// <para>
/// Deliberately one row per <em>identity</em> rather than one per sighting: a profile that sweeps its
/// range every five minutes reports the same estate every five minutes, and a row per message would
/// make the review queue unreadable within an hour. The row is upserted on each sighting — the counts
/// and <see cref="LastSeenAt"/> move, the decision does not.
/// </para>
/// <para>
/// It is also the ignore list. A rejected row stays forever and is matched again on every later scan,
/// which is the whole of "reject → never reappears": nothing deletes it, and the intake stops at it
/// before any CI matching runs.
/// </para>
/// </summary>
public sealed class DiscoveredDevice
{
    public Guid Id { get; set; }

    /// <summary>
    /// The identity this row speaks for, as <see cref="Features.Discovery.DiscoveryIdentity"/> spells
    /// it: <c>snmp:</c> a device's own name for itself, else <c>host:</c> its short reverse-DNS name,
    /// else <c>addr:</c> the address that answered. Unique, and rewritten in place when a later scan
    /// learns a better one — a device that gains an SNMP agent must not become a second row.
    /// </summary>
    public required string IdentityKey { get; set; }

    /// <summary>The IPv4 address that answered on the most recent sighting.</summary>
    public required string Address { get; set; }

    /// <summary>Reverse DNS as the scanner's resolver gave it, fully qualified.</summary>
    public string? Hostname { get; set; }

    public bool RespondedToPing { get; set; }

    /// <summary>
    /// Which fingerprint ports accepted a connection, as a jsonb array of numbers.
    /// <para>
    /// A TCP fingerprint cannot see a UDP service, so this is emphatically not "what the device
    /// serves": the simulator answers SNMP on 161 and reports an empty list. WP-4.1's hand-verification
    /// recorded the same trap.
    /// </para>
    /// </summary>
    public required string OpenPortsJson { get; set; }

    // What the agent said about itself, flattened. Every one is optional because every one genuinely
    // is — an agent that answers sysDescr and nothing else is common, and this is the difference
    // between a router and a printer.
    public string? SysName { get; set; }
    public string? SysDescription { get; set; }

    /// <summary>
    /// The vendor's numeric object identifier, dotted. Stored as the scanner sends it, which WP-4.1
    /// had to fix to be numeric: <c>prettyPrint</c> resolved it against whatever MIBs sat beside the
    /// scanner, so the same device rendered two ways and the fingerprint was not a key.
    /// </summary>
    public string? SysObjectId { get; set; }

    public string? SysLocation { get; set; }
    public string? SysContact { get; set; }
    public double? UptimeSeconds { get; set; }

    /// <summary>LLDP and CDP neighbours as the device reported them, as a jsonb array of objects.</summary>
    public required string NeighboursJson { get; set; }

    // Provenance: which scanner, which profile, which pass. Enough for a reviewer to answer "where did
    // this come from" without leaving the card.
    public required string DiscoveryName { get; set; }
    public Guid ScanProfileId { get; set; }
    public required string ScanProfileName { get; set; }
    public Guid LastScanId { get; set; }

    public DiscoveredDeviceStatus Status { get; set; } = DiscoveredDeviceStatus.Pending;

    /// <summary>
    /// The CI this resolves to, once anything does. Set on an automatic match and on approval, and
    /// null for as long as the row is Pending or Rejected.
    /// </summary>
    public Guid? CiId { get; set; }

    /// <summary>
    /// Which rung of the ladder placed it, so a reviewer can see <em>why</em> the platform believes
    /// this is that CI. A match nobody can interrogate is a guess presented as a fact.
    /// </summary>
    public DiscoveryMatchRule MatchRule { get; set; } = DiscoveryMatchRule.None;

    /// <summary>
    /// The CIs a rung found when it found more than one, as a jsonb array of ids. An ambiguous match
    /// is queued rather than resolved — picking one silently is how a CMDB fills with wrong facts.
    /// </summary>
    public required string ContenderCiIdsJson { get; set; }

    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }

    /// <summary>How many scans have reported this identity. One is a stranger; two hundred is furniture.</summary>
    public int SightingCount { get; set; }

    public string? ReviewedBy { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }

    /// <summary>Why a human approved or rejected it, in their words. Never generated.</summary>
    public string? ReviewNote { get; set; }
}

public enum DiscoveredDeviceStatus
{
    /// <summary>Nothing in the CMDB claims it and a human has not looked yet.</summary>
    Pending,

    /// <summary>The intake placed it against an existing CI on its own; no review was needed.</summary>
    Matched,

    /// <summary>A human approved it and a CI now exists. <see cref="DiscoveredDevice.CiId"/> says which.</summary>
    Approved,

    /// <summary>A human said this is not an asset. The row is the ignore list and is never deleted.</summary>
    Rejected,
}

/// <summary>
/// Which signal placed a discovery against a CI, strongest first. The order is the ladder the matcher
/// walks and is load-bearing: renumbering it re-ranks the heuristics.
/// </summary>
public enum DiscoveryMatchRule
{
    /// <summary>Nothing matched. The discovery is a stranger and goes to review.</summary>
    None,

    /// <summary>
    /// This row already resolved to a CI on an earlier scan. The cheapest and most certain rung,
    /// because it is a decision somebody already made rather than a heuristic re-run.
    /// </summary>
    Ledger,

    /// <summary>
    /// The address is one a monitored device already polls, so an operator has already asserted that
    /// this address is that CI. Read through <c>IMonitoredAddressDirectory</c>; Assets never queries
    /// the monitoring schema.
    /// </summary>
    MonitoredAddress,

    /// <summary>A network CI records this address as its management IP.</summary>
    ManagementIp,

    /// <summary>A server or virtual CI records this hostname, matched against sysName or reverse DNS.</summary>
    Hostname,

    /// <summary>A CI is named exactly what the device calls itself. The weakest rung that still fires.</summary>
    Name,

    /// <summary>
    /// A rung found more than one CI. Deliberately not a match: two CIs claiming one device is a
    /// question for a human, and the contenders travel on the review card so they can answer it.
    /// </summary>
    Ambiguous,
}

/// <summary>
/// What discovery last observed about a CI, kept beside the CI rather than written into it.
/// <para>
/// The split is the point. A scan <em>observes</em>; an operator <em>asserts</em>. Overwriting a CI's
/// recorded attributes with scanned ones would destroy exactly the difference WP-4.6 exists to report,
/// so a match refreshes this row and never touches the CI's own fields — the CMDB keeps saying what
/// somebody typed, this says what the network answered, and the drift between them is a later
/// package's whole subject.
/// </para>
/// </summary>
public sealed class CiDiscoveryFacts
{
    /// <summary>The CI these facts are about, and the primary key: one CI has one current observation.</summary>
    public Guid CiId { get; set; }

    public ConfigurationItem? Ci { get; set; }

    public required string Address { get; set; }
    public string? Hostname { get; set; }
    public bool RespondedToPing { get; set; }
    public required string OpenPortsJson { get; set; }

    public string? SysName { get; set; }

    /// <summary>
    /// The device's own description of itself — model, OS and firmware revision in one vendor-shaped
    /// string. This is the "firmware" the WP text asks discovery to keep current, kept verbatim rather
    /// than parsed: there is no format shared across vendors, and a regex that guesses a version out of
    /// arbitrary text produces a field that is confidently wrong instead of plainly raw.
    /// </summary>
    public string? SysDescription { get; set; }

    public string? SysObjectId { get; set; }
    public string? SysLocation { get; set; }
    public string? SysContact { get; set; }
    public double? UptimeSeconds { get; set; }
    public required string NeighboursJson { get; set; }

    public required string DiscoveryName { get; set; }
    public required string ScanProfileName { get; set; }
    public Guid LastScanId { get; set; }

    public DateTimeOffset FirstSeenAt { get; set; }

    /// <summary>The last-seen the WP text asks for: when a scan last had this CI answer.</summary>
    public DateTimeOffset LastSeenAt { get; set; }

    public int SightingCount { get; set; }
}
