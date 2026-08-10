namespace Contracts.Events;

/// <summary>
/// A registered poller has stopped speaking: nothing has arrived from it for
/// <see cref="MissedHeartbeats"/> of its own intervals. Raised once per silence, not once per
/// evaluation, so a poller that stays down does not emit a message a minute.
/// <para>
/// This is a fact, not an alert. WP-3.5 owns the alert state machine and WP-3.6 the ticket; both
/// consume this rather than re-deriving it.
/// </para>
/// </summary>
public sealed record PollerHeartbeatMissed(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid PollerId,
    string PollerName,
    string PollerGroup,
    DateTimeOffset LastHeartbeatAt,
    int MissedHeartbeats,
    int IntervalSeconds);
