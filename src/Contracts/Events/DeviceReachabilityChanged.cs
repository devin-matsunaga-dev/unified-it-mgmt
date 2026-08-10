namespace Contracts.Events;

/// <summary>
/// A monitored device started or stopped answering. Published on the transition only — a device that
/// stays down for an hour says so once, following the WP-3.2 rule that a poller silent for a day
/// emits one <see cref="PollerHeartbeatMissed"/> rather than one a minute.
/// <para>
/// The first observation after a poller starts is a transition too: the platform has no state for a
/// device it has never polled, so the poller states what it found rather than staying quiet until
/// something changes. The consequence is one event per device on a poller restart, which is the
/// price of the platform knowing anything at all after one.
/// </para>
/// <para>
/// This is a fact, not an alert. Nothing raises, tickets or notifies on it; WP-3.5's state machine
/// and WP-3.6's automation are its intended consumers.
/// </para>
/// </summary>
/// <param name="ConsecutiveFailures">
/// How many cycles in a row the device failed. On an outage it counts the failures so far — one on
/// the first, so a consumer can tell a dropped packet from a device gone for ten minutes. On a
/// recovery it is the length of the outage that just ended, because the cycles in between published
/// nothing and this event is the only place that number can be read.
/// </param>
public sealed record DeviceReachabilityChanged(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid DeviceId,
    Guid CiId,
    string Address,
    string PollerName,
    string PollerGroup,
    bool IsReachable,
    int ConsecutiveFailures,
    string? Error);
