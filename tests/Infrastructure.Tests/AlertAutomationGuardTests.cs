using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Modules.Helpdesk.Features.AlertTickets;

using StackExchange.Redis;

namespace Infrastructure.Tests;

/// <summary>
/// The guard when Redis cannot answer. No container: the multiplexer is pointed at a closed port,
/// which is what a Redis outage looks like from inside the process.
/// <para>
/// This is here because the live loop cannot reach this path. Hand-verification of WP-3.6 (2026-08-11)
/// stopped the Redis container and found that no alert reaches the automation at all during an
/// outage — WP-3.5's engine stalls first, holding telemetry unacknowledged — so the fallback is only
/// observable from here. The bound still has to hold, because a storm during a cache outage is when a
/// ticket flood does the most damage.
/// </para>
/// </summary>
public sealed class AlertAutomationGuardTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Fact]
    public async Task Evaluate_WithRedisUnreachableAndFewRecentTickets_AllowsFromTheStoredCount()
    {
        var guard = Guard(out var options);

        var decision = await guard.EvaluateAsync(
            "alert:device:rule", (_, _) => Task.FromResult(options.BreakerThreshold - 1), Now, CancellationToken.None);

        Assert.Equal(AutomationVerdict.Allowed, decision.Verdict);
        Assert.Contains("stored tickets", decision.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// The bound that must not disappear with the cache. At the threshold the automation stops even
    /// though nothing in Redis can say so.
    /// </summary>
    [Fact]
    public async Task Evaluate_WithRedisUnreachableAndAStormAlreadyTicketed_RefusesFromTheStoredCount()
    {
        var guard = Guard(out var options);

        var decision = await guard.EvaluateAsync(
            "alert:device:rule", (_, _) => Task.FromResult(options.BreakerThreshold), Now, CancellationToken.None);

        Assert.Equal(AutomationVerdict.BreakerOpen, decision.Verdict);
        Assert.False(decision.IsAllowed);
    }

    /// <summary>The window the fallback counts over is the breaker's own, not an invented one.</summary>
    [Fact]
    public async Task Evaluate_WithRedisUnreachable_CountsOverTheBreakerWindow()
    {
        var guard = Guard(out var options);
        DateTimeOffset? asked = null;

        await guard.EvaluateAsync(
            "alert:device:rule",
            (since, _) =>
            {
                asked = since;
                return Task.FromResult(0);
            },
            Now,
            CancellationToken.None);

        Assert.Equal(Now.AddSeconds(-options.BreakerWindowSeconds), asked);
    }

    [Fact]
    public async Task Evaluate_WithNoRuleKey_Throws()
    {
        var guard = Guard(out _);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            guard.EvaluateAsync(" ", (_, _) => Task.FromResult(0), Now, CancellationToken.None));
    }

    /// <summary>
    /// A multiplexer aimed at a closed port. <c>abortConnect=false</c> is what lets it be constructed
    /// at all; every command against it then fails the way a real outage fails.
    /// </summary>
    private static RedisAlertAutomationGuard Guard(out AlertTicketOptions options)
    {
        options = new AlertTicketOptions();
        var configuration = ConfigurationOptions.Parse("127.0.0.1:6"); // discard port: nothing listens
        configuration.AbortOnConnectFail = false;
        configuration.ConnectTimeout = 200;
        configuration.ConnectRetry = 1;
        configuration.SyncTimeout = 200;
        return new RedisAlertAutomationGuard(
            ConnectionMultiplexer.Connect(configuration),
            Options.Create(options),
            NullLogger<RedisAlertAutomationGuard>.Instance);
    }
}
