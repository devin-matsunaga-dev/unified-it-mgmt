namespace Contracts.Events;

/// <summary>
/// One poller reporting that it completed a cycle. This is the only message a poller publishes in
/// WP-3.2 and the only reason it holds bus credentials at all; telemetry arrives in WP-3.3.
/// <para>
/// <see cref="IntervalSeconds"/> travels with the beat rather than being configured on the server,
/// because the poller is the only thing that knows how often it intends to speak — and "missed N
/// heartbeats" is meaningless without that number.
/// </para>
/// </summary>
public sealed record PollerHeartbeat(
    Guid EventId,
    DateTimeOffset OccurredAt,
    string PollerName,
    string PollerGroup,
    string? AgentVersion,
    long ConfigVersion,
    int IntervalSeconds,
    int DeviceCount,
    long CycleNumber);
