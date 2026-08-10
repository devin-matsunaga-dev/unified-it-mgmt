using Modules.Monitoring.Data;
using Modules.Monitoring.Features.Heartbeats;

namespace Infrastructure.Tests;

/// <summary>
/// The whole "missed N heartbeats" matrix, decided without a database or a clock.
/// </summary>
public sealed class PollerHeartbeatEvaluatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);

    private const int Threshold = 2;
    private const int DefaultInterval = 15;

    [Fact]
    public void Plan_PollerWithinItsInterval_IsNotReported()
    {
        var poller = NewPoller(lastHeartbeat: Now.AddSeconds(-14), intervalSeconds: 15);

        Assert.Empty(Evaluate(poller));
    }

    /// <summary>One missed beat is a slow cycle or a dropped packet, not an outage.</summary>
    [Fact]
    public void Plan_PollerThatHasMissedOneInterval_IsNotReported()
    {
        var poller = NewPoller(lastHeartbeat: Now.AddSeconds(-29), intervalSeconds: 15);

        Assert.Empty(Evaluate(poller));
    }

    [Fact]
    public void Plan_PollerSilentForTheThreshold_IsReportedWithTheCount()
    {
        var poller = NewPoller(lastHeartbeat: Now.AddSeconds(-30), intervalSeconds: 15);

        var silent = Assert.Single(Evaluate(poller));

        Assert.Equal(poller, silent.Poller);
        Assert.Equal(2, silent.MissedHeartbeats);
        Assert.Equal(15, silent.IntervalSeconds);
    }

    [Fact]
    public void Plan_PollerSilentForLonger_ReportsHowManyItMissed()
    {
        var poller = NewPoller(lastHeartbeat: Now.AddMinutes(-2), intervalSeconds: 15);

        var silent = Assert.Single(Evaluate(poller));

        Assert.Equal(8, silent.MissedHeartbeats);
    }

    /// <summary>
    /// The interval is the poller's own, so a slow poller is judged by its own cycle rather than by
    /// a global number that would report it dead every pass.
    /// </summary>
    [Fact]
    public void Plan_SlowPoller_IsJudgedByItsOwnInterval()
    {
        var slow = NewPoller(lastHeartbeat: Now.AddSeconds(-100), intervalSeconds: 300);

        Assert.Empty(Evaluate(slow));
    }

    /// <summary>
    /// A registration that never started is a deployment problem, not an outage — and there is no
    /// interval to be late by, because the poller has never stated one.
    /// </summary>
    [Fact]
    public void Plan_PollerThatHasNeverSpoken_IsNotReported()
    {
        var poller = NewPoller(lastHeartbeat: null, intervalSeconds: null);

        Assert.Empty(Evaluate(poller));
    }

    /// <summary>Once per silence, not once per pass.</summary>
    [Fact]
    public void Plan_PollerAlreadyReported_IsNotReportedAgain()
    {
        var poller = NewPoller(lastHeartbeat: Now.AddMinutes(-5), intervalSeconds: 15);
        poller.HeartbeatMissedAt = Now.AddMinutes(-4);

        Assert.Empty(Evaluate(poller));
    }

    [Fact]
    public void Plan_DisabledPoller_IsNotReported()
    {
        var poller = NewPoller(lastHeartbeat: Now.AddMinutes(-5), intervalSeconds: 15);
        poller.IsEnabled = false;

        Assert.Empty(Evaluate(poller));
    }

    /// <summary>
    /// A poller reporting a nonsense interval is held to the configured one rather than trusted
    /// into never being reported at all.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(null)]
    public void Plan_PollerWithoutAUsableInterval_FallsBackToTheConfiguredOne(int? reported)
    {
        var poller = NewPoller(lastHeartbeat: Now.AddSeconds(-30), intervalSeconds: reported);

        var silent = Assert.Single(Evaluate(poller));

        Assert.Equal(DefaultInterval, silent.IntervalSeconds);
        Assert.Equal(2, silent.MissedHeartbeats);
    }

    [Fact]
    public void Plan_MixedFleet_ReportsOnlyTheQuietOnes()
    {
        var healthy = NewPoller("healthy", Now.AddSeconds(-5), 15);
        var quiet = NewPoller("quiet", Now.AddMinutes(-1), 15);
        var disabled = NewPoller("disabled", Now.AddMinutes(-1), 15);
        disabled.IsEnabled = false;

        var silent = Evaluate(healthy, quiet, disabled);

        Assert.Equal(["quiet"], silent.Select(entry => entry.Poller.Name));
    }

    [Fact]
    public void Plan_WithAThresholdBelowOne_IsRefused() =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PollerHeartbeatEvaluator.Plan([], Now, missedThreshold: 0, defaultIntervalSeconds: 15));

    private static IReadOnlyList<SilentPoller> Evaluate(params Poller[] pollers) =>
        PollerHeartbeatEvaluator.Plan(pollers, Now, Threshold, DefaultInterval);

    private static Poller NewPoller(
        string name = "poller-1",
        DateTimeOffset? lastHeartbeat = null,
        int? intervalSeconds = null) => new()
        {
            Id = Guid.CreateVersion7(),
            Name = name,
            PollerGroup = "default",
            RegisteredAt = Now.AddHours(-1),
            LastRegisteredAt = Now.AddHours(-1),
            LastHeartbeatAt = lastHeartbeat,
            HeartbeatIntervalSeconds = intervalSeconds,
        };
}
