namespace Contracts.Events;

/// <summary>
/// A human looked at something a scan found, said it is a real asset, and a CI now exists for it.
/// <para>
/// Published by Assets when a review-queue card is approved (WP-4.2). It exists because approving can
/// also mean "and start monitoring it", and Monitoring owns monitored devices while Assets owns CIs:
/// neither module may reference the other, so the enrolment crosses the boundary as a fact on the bus
/// rather than as a call. ARCHITECTURE §3 is explicit that a port is a read surface and never a write
/// path, which is why this is an event and not a second method on
/// <c>IMonitoredAddressDirectory</c>.
/// </para>
/// <para>
/// It is published on <em>every</em> approval, including the ones that asked for no monitoring, because
/// what happened is a fact either way: the CMDB gained a CI that nobody typed. WP-4.3's topology work
/// is the next thing likely to want it.
/// </para>
/// </summary>
/// <param name="DiscoveredDeviceId">The review-queue row that was approved, for tracing a CI back to its scan.</param>
/// <param name="CiId">The CI that now exists. Created before this was published, so it is always resolvable.</param>
/// <param name="Address">The address the scan found it on, and the address a monitored device would poll.</param>
/// <param name="MonitoringRequested">
/// Whether the approver ticked "monitor this". False means the CI is enough — plenty of discovered
/// hardware is inventory rather than something to watch, and enrolling it all would fill the alert
/// board with devices nobody chose to care about.
/// </param>
/// <param name="PollerGroup">
/// Which poller group should take it, or null for the platform default. Named by the approver because
/// a scanner has no idea which poller can reach the network it swept.
/// </param>
public sealed record DiscoveredDeviceApproved(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid DiscoveredDeviceId,
    Guid CiId,
    string Address,
    string? Hostname,
    bool MonitoringRequested,
    string? PollerGroup);
