using Modules.Monitoring.Data;

namespace Modules.Monitoring.Features.Heartbeats;

/// <summary>One poller judged to have gone quiet, and by how much.</summary>
public sealed record SilentPoller(Poller Poller, int MissedHeartbeats, int IntervalSeconds);

/// <summary>
/// Decides which pollers have gone quiet. Pure, so the whole matrix — never spoke, spoke a moment
/// ago, late, already reported, disabled — is unit-testable without a database or a clock.
/// </summary>
public static class PollerHeartbeatEvaluator
{
    /// <summary>
    /// A poller is quiet once nothing has arrived for <paramref name="missedThreshold"/> of its own
    /// intervals. Two rules keep this from being noisy:
    /// <list type="bullet">
    /// <item>a poller that has never spoken is skipped — it has no interval to be late by, and a
    /// registration that never started is a deployment problem rather than an outage;</item>
    /// <item>a poller already reported for this silence is skipped, so the event fires once per
    /// outage rather than once per pass.</item>
    /// </list>
    /// </summary>
    public static IReadOnlyList<SilentPoller> Plan(
        IEnumerable<Poller> pollers,
        DateTimeOffset now,
        int missedThreshold,
        int defaultIntervalSeconds)
    {
        ArgumentNullException.ThrowIfNull(pollers);
        ArgumentOutOfRangeException.ThrowIfLessThan(missedThreshold, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(defaultIntervalSeconds, 1);

        var silent = new List<SilentPoller>();
        foreach (var poller in pollers)
        {
            if (!poller.IsEnabled || poller.HeartbeatMissedAt is not null)
            {
                continue;
            }

            if (poller.LastHeartbeatAt is not { } lastHeartbeat)
            {
                continue;
            }

            // A poller that reported a nonsensical interval is held to the configured one rather
            // than trusted into never alerting.
            var interval = poller.HeartbeatIntervalSeconds is int reported && reported > 0
                ? reported
                : defaultIntervalSeconds;

            var elapsed = now - lastHeartbeat;
            var missed = (int)(elapsed.TotalSeconds / interval);
            if (missed >= missedThreshold)
            {
                silent.Add(new SilentPoller(poller, missed, interval));
            }
        }

        return silent;
    }
}
