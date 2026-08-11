using Modules.Monitoring.Data;

namespace Modules.Monitoring.Features.Dashboards;

/// <summary>One open alert, reduced to the four facts a tile is computed from.</summary>
public sealed record OpenAlertFact(
    AlertSeverity Severity,
    string Summary,
    DateTimeOffset RaisedAt,
    bool Acknowledged);

/// <summary>What the roll-up decided, before the device's own fields are attached to it.</summary>
public sealed record DeviceStatusSummary(
    DeviceStatus Status,
    AlertSeverity Severity,
    int OpenAlerts,
    int CriticalAlerts,
    int WarningAlerts,
    int AcknowledgedAlerts,
    string? Headline,
    DateTimeOffset? WorstAlertRaisedAt);

/// <summary>
/// One device's tile from its open alerts. Worst severity wins: a device is as bad as the worst thing
/// wrong with it, which is what makes a wall of tiles readable at a glance.
/// <para>
/// Pure on purpose — no database, no clock, no configuration — so every rule below is unit-testable
/// and the service above it does nothing but fetch.
/// </para>
/// </summary>
public static class DeviceStatusRollup
{
    public static DeviceStatusSummary Summarise(
        bool isEnabled,
        DateTimeOffset? lastTelemetryAt,
        IReadOnlyList<OpenAlertFact> openAlerts)
    {
        ArgumentNullException.ThrowIfNull(openAlerts);

        var critical = openAlerts.Count(alert => alert.Severity == AlertSeverity.Critical);
        var warning = openAlerts.Count(alert => alert.Severity == AlertSeverity.Warning);
        var acknowledged = openAlerts.Count(alert => alert.Acknowledged);

        // Worst first, then oldest: of two Criticals the one that has been wrong longest is the one
        // whose sentence belongs on the tile.
        var worst = openAlerts
            .OrderByDescending(alert => alert.Severity)
            .ThenBy(alert => alert.RaisedAt)
            .FirstOrDefault();

        // An open alert at Ok is not a contradiction: WP-3.5 leaves a muted or flapping rule's row
        // open at Ok until the next reading after suppression lifts reconciles it. It is counted as
        // open — it is a row somebody will find — but it colours nothing.
        var severity = worst?.Severity ?? AlertSeverity.Ok;

        var status = (isEnabled, severity) switch
        {
            (false, _) => DeviceStatus.Disabled,
            (true, AlertSeverity.Critical) => DeviceStatus.Critical,
            (true, AlertSeverity.Warning) => DeviceStatus.Warning,
            // Nothing wrong and nothing measured. An alert is itself evidence the device reported at
            // some point, so this can only be reached when there is no alert to go on either.
            (true, _) when lastTelemetryAt is null && openAlerts.Count == 0 => DeviceStatus.Unknown,
            _ => DeviceStatus.Ok,
        };

        return new DeviceStatusSummary(
            status,
            severity,
            openAlerts.Count,
            critical,
            warning,
            acknowledged,
            worst?.Summary,
            worst?.RaisedAt);
    }
}
