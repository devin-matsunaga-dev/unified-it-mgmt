using Modules.Monitoring.Data;

namespace Modules.Monitoring.Features.Runbooks;

public enum RunbookVerdict
{
    /// <summary>Run it.</summary>
    Allowed,

    /// <summary>Auto-remediation is switched off estate-wide, or this runbook is disabled.</summary>
    Disabled,

    /// <summary>This runbook has run its allowance for the window.</summary>
    RateLimited,
}

public sealed record RunbookDecision(RunbookVerdict Verdict, string Reason)
{
    public bool IsAllowed => Verdict == RunbookVerdict.Allowed;
}

/// <summary>
/// The per-runbook rate limit, as a pure function of a count somebody else took.
/// <para>
/// Deliberately <b>not</b> in Redis, which is where ARCHITECTURE §5 puts rate-limit state and where
/// WP-3.6 put the alert→ticket one. The difference is what the two bounds protect. WP-3.6 bounds
/// tickets, and its Redis path may fail open because the durable dedupe row already caps the damage at
/// one extra ticket. This bounds <em>executions on real machines</em>, and ARCHITECTURE §5 says Redis
/// is explicitly not a source of truth and must survive being flushed — a bound that a
/// <c>FLUSHALL</c> resets is not a bound. Counting rows in <c>monitoring.runbook_executions</c> is one
/// indexed count against data that is already durable, on a path that runs once per remediation rather
/// than once per reading, so the cost of being exact here is nothing worth saving.
/// </para>
/// </summary>
public static class RunbookRateLimit
{
    /// <summary>The instant the counting window opens. Fixed length, ending now — a sliding window.</summary>
    public static DateTimeOffset WindowStart(Runbook runbook, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(runbook);
        return now.AddMinutes(-Math.Max(runbook.RateLimitWindowMinutes, 1));
    }

    /// <param name="recentExecutions">
    /// Executions of this runbook requested since <see cref="WindowStart"/>, counted from the table.
    /// Every status counts, including the ones that failed: a runbook failing forty times an hour is
    /// exactly the storm this stops, and counting only successes would make failure the cheap case.
    /// </param>
    public static RunbookDecision Evaluate(
        Runbook runbook,
        RunbookOptions options,
        int recentExecutions,
        bool isAutomatic)
    {
        ArgumentNullException.ThrowIfNull(runbook);
        ArgumentNullException.ThrowIfNull(options);

        if (!options.Enabled)
        {
            return new(RunbookVerdict.Disabled, "auto-remediation is switched off for this platform");
        }

        if (isAutomatic && !options.AutomaticTriggersEnabled)
        {
            return new(RunbookVerdict.Disabled, "alerts are not permitted to start runbooks on this platform");
        }

        if (!runbook.IsEnabled)
        {
            return new(RunbookVerdict.Disabled, $"the runbook '{runbook.Key}' is disabled");
        }

        var allowance = Math.Max(runbook.MaxExecutionsPerWindow, 0);
        if (recentExecutions >= allowance)
        {
            var window = Math.Max(runbook.RateLimitWindowMinutes, 1);
            return new(
                RunbookVerdict.RateLimited,
                $"'{runbook.Key}' has already run {recentExecutions} time(s) in the last {window} minute(s), and its limit is {allowance}");
        }

        return new(RunbookVerdict.Allowed, "within limits");
    }
}
