using Microsoft.EntityFrameworkCore;

using Modules.Monitoring.Data;

using Platform.Integration;

namespace Modules.Monitoring.Features.Dashboards;

public interface IStatusBoardService
{
    Task<StatusBoardResponse> GetAsync(StatusBoardRequest request, CancellationToken cancellationToken);

    /// <summary>One device's tile, for a live update. Null when the device has been deleted.</summary>
    Task<DeviceStatusTile?> GetTileAsync(Guid deviceId, CancellationToken cancellationToken);
}

/// <summary>
/// The status board: one tile per monitored device, coloured by the worst thing currently wrong with
/// it. The decisions all live in <see cref="DeviceStatusRollup"/>; this fetches the three things the
/// roll-up needs and reads the CI names through the port.
/// </summary>
public sealed class StatusBoardService(
    MonitoringDbContext dbContext,
    ICiDirectory ciDirectory) : IStatusBoardService
{
    private const int MaximumPageSize = 200;

    /// <summary>
    /// How far back "last reported" is looked for. Bounded so the query prunes hypertable chunks
    /// instead of scanning the retention window; a device silent for longer than this reads as one
    /// that has never reported, which is the same tile and the same conclusion.
    /// </summary>
    private static readonly TimeSpan TelemetryLookback = TimeSpan.FromDays(2);

    public async Task<StatusBoardResponse> GetAsync(
        StatusBoardRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var page = Math.Max(request.Page, 1);
        var pageSize = Math.Clamp(request.PageSize, 1, MaximumPageSize);

        var query = dbContext.MonitoredDevices.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(request.PollerGroup))
        {
            query = query.Where(device => device.PollerGroup == request.PollerGroup);
        }

        // Address only, for the same reason the device list searches on it alone: a CI's name is in
        // the Assets schema and the port answers by id, never by term.
        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var term = $"%{request.Search.Trim()}%";
            query = query.Where(device => EF.Functions.ILike(device.Address, term));
        }

        var total = await query.CountAsync(cancellationToken);
        var devices = await query
            .OrderBy(device => device.Address).ThenBy(device => device.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(device => new DeviceFacts(device, device.Checks.Count))
            .ToListAsync(cancellationToken);

        var tiles = await BuildAsync(devices, cancellationToken);

        return new StatusBoardResponse(
            tiles,
            total,
            page,
            pageSize,
            await CountAsync(cancellationToken));
    }

    public async Task<DeviceStatusTile?> GetTileAsync(Guid deviceId, CancellationToken cancellationToken)
    {
        var device = await dbContext.MonitoredDevices.AsNoTracking()
            .Where(row => row.Id == deviceId)
            .Select(row => new DeviceFacts(row, row.Checks.Count))
            .SingleOrDefaultAsync(cancellationToken);

        return device is null ? null : (await BuildAsync([device], cancellationToken)).SingleOrDefault();
    }

    private async Task<List<DeviceStatusTile>> BuildAsync(
        List<DeviceFacts> devices,
        CancellationToken cancellationToken)
    {
        if (devices.Count == 0)
        {
            return [];
        }

        var deviceIds = devices.Select(entry => entry.Device.Id).ToList();

        var openAlerts = (await dbContext.Alerts.AsNoTracking()
                .Where(alert => deviceIds.Contains(alert.DeviceId) && alert.Status == AlertStatus.Open)
                .Select(alert => new
                {
                    alert.DeviceId,
                    alert.Severity,
                    alert.Summary,
                    alert.RaisedAt,
                    Acknowledged = alert.AcknowledgedAt != null,
                })
                .ToListAsync(cancellationToken))
            .GroupBy(alert => alert.DeviceId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<OpenAlertFact>)
                    [.. group.Select(alert => new OpenAlertFact(
                        alert.Severity, alert.Summary, alert.RaisedAt, alert.Acknowledged))]);

        var since = DateTimeOffset.UtcNow - TelemetryLookback;
        var lastSeen = await dbContext.DeviceMetrics.AsNoTracking()
            .Where(metric => deviceIds.Contains(metric.DeviceId) && metric.Time >= since)
            .GroupBy(metric => metric.DeviceId)
            .Select(group => new { DeviceId = group.Key, Last = group.Max(metric => metric.Time) })
            .ToDictionaryAsync(entry => entry.DeviceId, entry => entry.Last, cancellationToken);

        var cis = (await ciDirectory.GetSummariesAsync(
                [.. devices.Select(entry => entry.Device.CiId).Distinct()], cancellationToken))
            .ToDictionary(ci => ci.Id);

        return
        [
            .. devices.Select(entry =>
            {
                var device = entry.Device;
                var alerts = openAlerts.TryGetValue(device.Id, out var found) ? found : [];
                var last = lastSeen.TryGetValue(device.Id, out var seen) ? seen : (DateTimeOffset?)null;
                var summary = DeviceStatusRollup.Summarise(device.IsEnabled, last, alerts);
                cis.TryGetValue(device.CiId, out var ci);

                return new DeviceStatusTile(
                    device.Id,
                    device.CiId,
                    ci?.Name,
                    ci?.Type,
                    ci?.SiteName,
                    device.Address,
                    device.PollerGroup,
                    device.IsEnabled,
                    summary.Status,
                    summary.Severity,
                    summary.OpenAlerts,
                    summary.CriticalAlerts,
                    summary.WarningAlerts,
                    summary.AcknowledgedAlerts,
                    entry.CheckCount,
                    summary.Headline,
                    summary.WorstAlertRaisedAt,
                    last);
            }),
        ];
    }

    /// <summary>
    /// The estate-wide tally behind the KPI row. It counts every device rather than the page, so the
    /// same query has to run the roll-up over all of them — which is why it reads only the two columns
    /// the roll-up needs rather than building tiles nobody will see.
    /// </summary>
    private async Task<StatusBoardCounts> CountAsync(CancellationToken cancellationToken)
    {
        var devices = await dbContext.MonitoredDevices.AsNoTracking()
            .Select(device => new { device.Id, device.IsEnabled })
            .ToListAsync(cancellationToken);
        if (devices.Count == 0)
        {
            return new StatusBoardCounts(0, 0, 0, 0, 0, 0);
        }

        var worst = await dbContext.Alerts.AsNoTracking()
            .Where(alert => alert.Status == AlertStatus.Open)
            .Select(alert => new { alert.DeviceId, alert.Severity })
            .ToListAsync(cancellationToken);
        var bySeverity = worst
            .GroupBy(alert => alert.DeviceId)
            .ToDictionary(group => group.Key, group => group.Max(alert => alert.Severity));

        var since = DateTimeOffset.UtcNow - TelemetryLookback;
        var reported = await dbContext.DeviceMetrics.AsNoTracking()
            .Where(metric => metric.Time >= since)
            .Select(metric => metric.DeviceId)
            .Distinct()
            .ToListAsync(cancellationToken);
        var seen = reported.ToHashSet();

        var statuses = devices
            .Select(device => DeviceStatusRollup.Summarise(
                device.IsEnabled,
                seen.Contains(device.Id) ? DateTimeOffset.UtcNow : null,
                bySeverity.TryGetValue(device.Id, out var severity)
                    ? [new OpenAlertFact(severity, string.Empty, DateTimeOffset.UtcNow, false)]
                    : [])
                .Status)
            .ToList();

        return new StatusBoardCounts(
            devices.Count,
            statuses.Count(status => status is DeviceStatus.Ok),
            statuses.Count(status => status is DeviceStatus.Warning),
            statuses.Count(status => status is DeviceStatus.Critical),
            statuses.Count(status => status is DeviceStatus.Unknown),
            statuses.Count(status => status is DeviceStatus.Disabled));
    }

    private sealed record DeviceFacts(MonitoredDevice Device, int CheckCount);
}
