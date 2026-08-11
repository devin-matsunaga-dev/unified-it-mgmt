using Modules.Monitoring.Data;
using Modules.Monitoring.Features.Dashboards;

namespace Infrastructure.Tests;

/// <summary>
/// The status board's one decision, tested without a database: what colour is this device, given its
/// open alerts and whether it has ever reported.
/// </summary>
public sealed class DeviceStatusRollupTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Summarise_WithNoAlertsAndRecentTelemetry_IsOk()
    {
        var summary = DeviceStatusRollup.Summarise(isEnabled: true, Now, []);

        Assert.Equal(DeviceStatus.Ok, summary.Status);
        Assert.Equal(AlertSeverity.Ok, summary.Severity);
        Assert.Equal(0, summary.OpenAlerts);
        Assert.Null(summary.Headline);
    }

    /// <summary>
    /// A device nobody has ever heard from is not a healthy device. Answering "Ok" here would be the
    /// same lie as printing 0 for a count that failed to load (WP-2.11).
    /// </summary>
    [Fact]
    public void Summarise_WithNoTelemetryEver_IsUnknownRatherThanOk()
    {
        var summary = DeviceStatusRollup.Summarise(isEnabled: true, lastTelemetryAt: null, []);

        Assert.Equal(DeviceStatus.Unknown, summary.Status);
    }

    /// <summary>Worst wins: one Critical among four Warnings is what the tile has to say.</summary>
    [Fact]
    public void Summarise_WithMixedSeverities_TakesTheWorstAndItsSummary()
    {
        var summary = DeviceStatusRollup.Summarise(isEnabled: true, Now,
        [
            new OpenAlertFact(AlertSeverity.Warning, "CPU is high", Now.AddMinutes(-30), false),
            new OpenAlertFact(AlertSeverity.Critical, "Host is unreachable", Now.AddMinutes(-5), false),
            new OpenAlertFact(AlertSeverity.Warning, "Memory is high", Now.AddMinutes(-10), false),
        ]);

        Assert.Equal(DeviceStatus.Critical, summary.Status);
        Assert.Equal(AlertSeverity.Critical, summary.Severity);
        Assert.Equal("Host is unreachable", summary.Headline);
        Assert.Equal(3, summary.OpenAlerts);
        Assert.Equal(1, summary.CriticalAlerts);
        Assert.Equal(2, summary.WarningAlerts);
    }

    /// <summary>Of two equally bad things, the one that has been wrong longest is the headline.</summary>
    [Fact]
    public void Summarise_WithTwoCriticals_HeadlinesTheOlderOne()
    {
        var older = Now.AddHours(-3);
        var summary = DeviceStatusRollup.Summarise(isEnabled: true, Now,
        [
            new OpenAlertFact(AlertSeverity.Critical, "Newer", Now.AddMinutes(-1), false),
            new OpenAlertFact(AlertSeverity.Critical, "Older", older, false),
        ]);

        Assert.Equal("Older", summary.Headline);
        Assert.Equal(older, summary.WorstAlertRaisedAt);
    }

    /// <summary>
    /// An acknowledgement says somebody is dealing with it, not that it is better. The tile stays the
    /// colour it was; only the count of claimed alerts moves.
    /// </summary>
    [Fact]
    public void Summarise_WhenEveryAlertIsAcknowledged_KeepsItsSeverity()
    {
        var summary = DeviceStatusRollup.Summarise(isEnabled: true, Now,
        [
            new OpenAlertFact(AlertSeverity.Critical, "Host is unreachable", Now.AddMinutes(-5), true),
        ]);

        Assert.Equal(DeviceStatus.Critical, summary.Status);
        Assert.Equal(1, summary.AcknowledgedAlerts);
    }

    /// <summary>
    /// WP-3.5 leaves a muted or flapping rule's row Open at severity Ok until the next reading after
    /// suppression lifts reconciles it. That is a row somebody will find, so it is counted — but it
    /// is not a problem, so it colours nothing.
    /// </summary>
    [Fact]
    public void Summarise_WithAnOpenAlertAtOk_CountsItWithoutColouringTheTile()
    {
        var summary = DeviceStatusRollup.Summarise(isEnabled: true, Now,
        [
            new OpenAlertFact(AlertSeverity.Ok, "Recovered while suppressed", Now.AddMinutes(-2), false),
        ]);

        Assert.Equal(DeviceStatus.Ok, summary.Status);
        Assert.Equal(AlertSeverity.Ok, summary.Severity);
        Assert.Equal(1, summary.OpenAlerts);
    }

    /// <summary>
    /// A disabled device leaves every poller's configuration, so nothing will ever report against it
    /// and its open alerts can never clear. They are still counted rather than hidden — a stale alert
    /// nobody can see is worse than one labelled as belonging to a device that is switched off.
    /// </summary>
    [Fact]
    public void Summarise_WhenDisabled_ReadsDisabledButStillCarriesItsStaleAlerts()
    {
        var summary = DeviceStatusRollup.Summarise(isEnabled: false, Now,
        [
            new OpenAlertFact(AlertSeverity.Critical, "Host is unreachable", Now.AddHours(-9), false),
        ]);

        Assert.Equal(DeviceStatus.Disabled, summary.Status);
        Assert.Equal(AlertSeverity.Critical, summary.Severity);
        Assert.Equal(1, summary.CriticalAlerts);
    }

    /// <summary>
    /// An alert is itself proof the device reported at some point, so a device with an open alert is
    /// never Unknown even when the telemetry lookback window has since gone quiet.
    /// </summary>
    [Fact]
    public void Summarise_WithAnAlertButNoRecentTelemetry_IsNotUnknown()
    {
        var summary = DeviceStatusRollup.Summarise(isEnabled: true, lastTelemetryAt: null,
        [
            new OpenAlertFact(AlertSeverity.Critical, "Host is unreachable", Now.AddDays(-4), false),
        ]);

        Assert.Equal(DeviceStatus.Critical, summary.Status);
    }
}
