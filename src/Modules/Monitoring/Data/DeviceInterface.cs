namespace Modules.Monitoring.Data;

/// <summary>
/// One interface of one monitored device, as the last poll found it — <c>monitoring.device_interfaces</c>.
/// <para>
/// Current state, not history, so this is a sibling of <see cref="DeviceInventoryFact"/> rather than
/// of <see cref="DeviceMetric"/>: one row per interface, overwritten in place, and taken away with
/// the device. The series behind every number here is in the hypertable, keyed by the metric name
/// the poller published it under; what this row exists for is the question a hypertable answers
/// badly — "what are this switch's ports doing right now", in one read, without a query per port.
/// </para>
/// <para>
/// Nothing creates one of these but ingestion. There is no API to add an interface and no form to
/// type one in: an interface is a fact about a device that the device itself reports, and one an
/// operator could invent would be a row that every poll disagrees with.
/// </para>
/// </summary>
public sealed class DeviceInterface
{
    public Guid DeviceId { get; set; }

    public MonitoredDevice Device { get; set; } = null!;

    /// <summary>
    /// The device's own ifIndex. Stable while the agent is up and famously not stable across a
    /// reboot on some platforms, which is why the name travels on every poll rather than once.
    /// </summary>
    public int IfIndex { get; set; }

    /// <summary>ifName where the device has one, else ifDescr — "Gi0/1" rather than "GigabitEthernet0/1".</summary>
    public string? Name { get; set; }

    /// <summary>ifAlias: what somebody typed on the switch to say what the cable goes to.</summary>
    public string? Alias { get; set; }

    /// <summary>ifPhysAddress, verbatim. Not parsed, and not matched against anything — see WP-4.2.</summary>
    public string? MacAddress { get; set; }

    /// <summary>ifType, as IANA numbers them (6 is ethernetCsmacd, 24 softwareLoopback).</summary>
    public int? InterfaceType { get; set; }

    public InterfaceStatus AdminStatus { get; set; } = InterfaceStatus.Unknown;

    public InterfaceStatus OperStatus { get; set; } = InterfaceStatus.Unknown;

    /// <summary>From ifHighSpeed where the device answers it, else ifSpeed. Null on a port with no speed.</summary>
    public long? SpeedBitsPerSecond { get; set; }

    /// <summary>
    /// The last rates the poller derived, denormalised onto the row so the interface table renders
    /// from one query. Null until the poller has seen the interface twice — a counter is a total, and
    /// one reading of a total is not a rate.
    /// </summary>
    public double? BitsInPerSecond { get; set; }

    public double? BitsOutPerSecond { get; set; }

    public double? UtilisationPercent { get; set; }

    public double? ErrorsInPerSecond { get; set; }

    public double? ErrorsOutPerSecond { get; set; }

    public double? DiscardsInPerSecond { get; set; }

    public double? DiscardsOutPerSecond { get; set; }

    /// <summary>The check whose poll last touched this row, so the page can chart the right series.</summary>
    public Guid CheckId { get; set; }

    /// <summary>When the poller read it, not when this row was written.</summary>
    public DateTimeOffset ObservedAt { get; set; }
}

/// <summary>
/// IF-MIB's ifOperStatus and ifAdminStatus, which share one enumeration.
/// <para>
/// Numbered as the MIB numbers them, because the poller publishes the number and this is what it
/// means. A value the MIB does not define — a vendor's private extension — reads as
/// <see cref="Unknown"/> rather than failing the ingestion of everything else in the batch.
/// </para>
/// </summary>
public enum InterfaceStatus
{
    Unknown = 0,
    Up = 1,
    Down = 2,
    Testing = 3,
    /// <summary>The MIB's own "unknown": the agent cannot tell. Distinct from a value it never sent.</summary>
    NotReported = 4,
    Dormant = 5,
    NotPresent = 6,
    LowerLayerDown = 7,
}
