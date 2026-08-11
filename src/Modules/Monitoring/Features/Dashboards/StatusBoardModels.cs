using Modules.Monitoring.Data;

namespace Modules.Monitoring.Features.Dashboards;

/// <summary>
/// What one tile on the status board says. Deliberately not the same thing as an alert severity:
/// a device can be disabled or never polled, and neither of those is "healthy".
/// </summary>
public enum DeviceStatus
{
    /// <summary>Polled, and nothing is wrong.</summary>
    Ok,

    Warning,

    Critical,

    /// <summary>
    /// Enabled but has never reported a reading. A tile that said "Ok" here would be claiming the
    /// device is healthy on the strength of no evidence at all — the same rule WP-2.11 applied when
    /// it made a failed KPI count read "Unavailable" rather than 0.
    /// </summary>
    Unknown,

    /// <summary>
    /// Switched off by an operator, so no poller holds it and no check will ever complete. Its open
    /// alerts are still counted on the tile: disabling a device stops the telemetry that would clear
    /// them, so they sit open, and hiding them would make a stale alert invisible.
    /// </summary>
    Disabled,
}

public sealed record StatusBoardRequest(
    string? Search = null,
    string? PollerGroup = null,
    int Page = 1,
    int PageSize = 25);

/// <param name="Severity">
/// The worst severity among the device's open alerts, which is what colours the tile. Independent of
/// <paramref name="Status"/>: a disabled device with a stale Critical alert reads Disabled and
/// Critical at once, and both are true.
/// </param>
/// <param name="AcknowledgedAlerts">
/// How many of the open alerts somebody has claimed. An acknowledgement never changes
/// <paramref name="Severity"/> — the estate is no healthier for having been noticed.
/// </param>
/// <param name="LastTelemetryAt">
/// The most recent reading of any metric on the device, or null if it has never reported. Read from
/// the WP-3.4 hypertable, so it is the poller's own view of when it last got an answer.
/// </param>
public sealed record DeviceStatusTile(
    Guid DeviceId,
    Guid CiId,
    string? CiName,
    string? CiType,
    string? SiteName,
    string Address,
    string PollerGroup,
    bool IsEnabled,
    DeviceStatus Status,
    AlertSeverity Severity,
    int OpenAlerts,
    int CriticalAlerts,
    int WarningAlerts,
    int AcknowledgedAlerts,
    int CheckCount,
    string? Headline,
    DateTimeOffset? WorstAlertRaisedAt,
    DateTimeOffset? LastTelemetryAt);

public sealed record StatusBoardResponse(
    IReadOnlyList<DeviceStatusTile> Items,
    int Total,
    int Page,
    int PageSize,
    StatusBoardCounts Counts);

/// <summary>
/// The board's KPI row, counted over the whole estate rather than the page — so paging cannot change
/// how many devices are said to be down.
/// </summary>
public sealed record StatusBoardCounts(
    int Devices,
    int Ok,
    int Warning,
    int Critical,
    int Unknown,
    int Disabled);
