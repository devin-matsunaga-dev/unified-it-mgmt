namespace Modules.Monitoring.Features.Heartbeats;

/// <summary>
/// How patient the platform is with a quiet poller. Bound from <c>Monitoring:Heartbeat</c>.
/// </summary>
public sealed class PollerHeartbeatOptions
{
    public const string SectionName = "Monitoring:Heartbeat";

    /// <summary>
    /// How many of a poller's own intervals may pass in silence before it is reported. Two: one
    /// missed beat is a slow cycle or a dropped packet, two is a poller that is not there.
    /// </summary>
    public int MissedThreshold { get; set; } = 2;

    /// <summary>
    /// The interval assumed for a poller that has not reported one. Only reached if a heartbeat
    /// arrives with a nonsensical interval, since every poller states its own.
    /// </summary>
    public int DefaultIntervalSeconds { get; set; } = 15;

    /// <summary>
    /// How often the evaluation pass runs. It is the granularity of detection, not its threshold:
    /// a poller is reported between <c>MissedThreshold</c> intervals and that plus this.
    /// </summary>
    public int EvaluationIntervalSeconds { get; set; } = 10;
}
