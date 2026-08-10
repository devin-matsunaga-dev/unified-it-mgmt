using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using StackExchange.Redis;

namespace Modules.Helpdesk.Features.AlertTickets;

public enum AutomationVerdict
{
    /// <summary>Open the ticket.</summary>
    Allowed,

    /// <summary>This rule has opened its allowance of tickets this minute.</summary>
    RateLimited,

    /// <summary>The breaker was already open when this alert arrived.</summary>
    BreakerOpen,

    /// <summary>This alert is the one that tripped the breaker. Exactly one alert ever sees this per trip — it is what notifies the admin.</summary>
    BreakerTripped,
}

public sealed record AutomationDecision(AutomationVerdict Verdict, string Reason)
{
    public bool IsAllowed => Verdict == AutomationVerdict.Allowed;
}

public interface IAlertAutomationGuard
{
    /// <summary>
    /// Whether this raise may open a ticket, and why not if it may not. Called once per raise that
    /// would otherwise create a ticket — an annotation on a ticket that already exists is not counted,
    /// because the WP bounds tickets rather than comments.
    /// </summary>
    /// <param name="durableRecentTicketCount">
    /// How many tickets the automation has opened inside the breaker window, answered from the
    /// database. Only called when Redis cannot answer — the bound has to survive a cache outage or it
    /// is not a bound.
    /// </param>
    Task<AutomationDecision> EvaluateAsync(
        string ruleKey,
        Func<DateTimeOffset, CancellationToken, Task<int>> durableRecentTicketCount,
        DateTimeOffset now,
        CancellationToken cancellationToken);
}

/// <summary>
/// The rate limit and the circuit breaker, in Redis, which is where ARCHITECTURE §5 puts rate-limit
/// state. Neither is source-of-truth data: losing them costs a window of counting, and the durable
/// <c>helpdesk.alert_tickets</c> row is what actually stops a duplicate ticket.
/// <para>
/// A Redis failure does not disable the bound. The rate limit is allowed through — a per-rule ticket
/// is already capped by the unique dedupe row, so the worst case is one extra ticket for a rule that
/// flapped — but the breaker falls back to counting rows in the window, because a storm during a
/// cache outage is precisely when a ticket flood would do the most damage.
/// </para>
/// </summary>
public sealed class RedisAlertAutomationGuard(
    IConnectionMultiplexer redis,
    IOptions<AlertTicketOptions> options,
    ILogger<RedisAlertAutomationGuard> logger) : IAlertAutomationGuard
{
    private const string KeyPrefix = "helpdesk:alert-ticket";

    public async Task<AutomationDecision> EvaluateAsync(
        string ruleKey,
        Func<DateTimeOffset, CancellationToken, Task<int>> durableRecentTicketCount,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ruleKey);
        ArgumentNullException.ThrowIfNull(durableRecentTicketCount);
        cancellationToken.ThrowIfCancellationRequested();

        var settings = options.Value;
        try
        {
            var database = redis.GetDatabase();

            if (await database.KeyExistsAsync(BreakerKey))
            {
                return new(AutomationVerdict.BreakerOpen,
                    $"the automation circuit breaker is open (more than {settings.BreakerThreshold} tickets in {settings.BreakerWindowSeconds}s)");
            }

            var rate = await CountAsync(
                database, $"{KeyPrefix}:rate:{ruleKey}", TimeSpan.FromMinutes(1));
            if (rate > settings.RateLimitPerRulePerMinute)
            {
                return new(AutomationVerdict.RateLimited,
                    $"this rule has already opened {settings.RateLimitPerRulePerMinute} tickets in the last minute");
            }

            var window = await CountAsync(
                database, WindowKey, TimeSpan.FromSeconds(settings.BreakerWindowSeconds));
            if (window > settings.BreakerThreshold)
            {
                // SETNX: whichever consumer crosses the line first owns the trip, so the admin is told
                // once however many alerts arrive in the same instant.
                var tripped = await database.StringSetAsync(
                    BreakerKey, now.ToString("O"),
                    TimeSpan.FromSeconds(settings.BreakerCooldownSeconds), When.NotExists);
                if (tripped)
                {
                    // The window is dropped with the trip, so the cooldown ends on a fresh count. Left
                    // in place it would still be over the threshold when the breaker expires and would
                    // re-trip on the next alert, which is a breaker that never closes.
                    await database.KeyDeleteAsync(WindowKey);
                }

                return new(
                    tripped ? AutomationVerdict.BreakerTripped : AutomationVerdict.BreakerOpen,
                    $"more than {settings.BreakerThreshold} tickets were opened in {settings.BreakerWindowSeconds}s");
            }

            return new(AutomationVerdict.Allowed, "within limits");
        }
        catch (RedisException error)
        {
            logger.LogWarning(error,
                "Alert automation limits could not be read from Redis; falling back to the stored ticket count.");
            var since = now.AddSeconds(-settings.BreakerWindowSeconds);
            var recent = await durableRecentTicketCount(since, cancellationToken);
            return recent >= settings.BreakerThreshold
                ? new(AutomationVerdict.BreakerOpen,
                    $"{recent} tickets were opened in the last {settings.BreakerWindowSeconds}s and the automation limits are unreadable")
                : new(AutomationVerdict.Allowed, "within limits (counted from stored tickets)");
        }
    }

    /// <summary>
    /// A fixed window rather than a sliding one: one INCR plus one EXPIRE on the first increment, and
    /// the recovery from a miscount is that the window ends. A sliding window costs a sorted set per
    /// rule to make the boundary a little fairer than a bound nobody is measuring to the second.
    /// </summary>
    private static async Task<long> CountAsync(IDatabase database, string key, TimeSpan window)
    {
        var count = await database.StringIncrementAsync(key);
        if (count == 1)
        {
            await database.KeyExpireAsync(key, window);
        }

        return count;
    }

    /// <summary>The keys, named here so a test can reset exactly these and nothing else — a FLUSHALL would take every other test's state with it.</summary>
    public const string BreakerKey = $"{KeyPrefix}:breaker-open";

    public const string WindowKey = $"{KeyPrefix}:window";
}
