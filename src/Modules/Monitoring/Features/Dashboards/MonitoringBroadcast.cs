using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using Modules.Monitoring.Data;
using Modules.Monitoring.Features.Alerting;

namespace Modules.Monitoring.Features.Dashboards;

/// <summary>
/// The way out to a browser. Implemented in <c>Web.Host</c> over a SignalR hub, because ARCHITECTURE
/// §2 puts every hub in the host and a module may not reference it — the same shape as the
/// <c>Platform/Integration</c> ports, one direction further out.
/// <para>
/// A push is a projection of state that is already committed, never a fact in its own right. That is
/// why it does not go through the outbox: nothing downstream depends on it, a browser that misses one
/// re-reads the board on reconnect, and a live view that lagged a durable event queue would not be
/// live. Every durable consequence of an alert still travels as <c>AlertRaised</c>/<c>AlertCleared</c>
/// through the outbox exactly as before.
/// </para>
/// </summary>
public interface IMonitoringBroadcaster
{
    Task AlertChangedAsync(AlertResponse alert, CancellationToken cancellationToken);

    Task DeviceStatusChangedAsync(DeviceStatusTile tile, CancellationToken cancellationToken);
}

/// <summary>
/// What a host without a hub does. Registered by the module so a test host, a seeder or a future
/// worker can construct the alert engine without knowing SignalR exists; <c>Web.Host</c> replaces it.
/// </summary>
public sealed class NullMonitoringBroadcaster : IMonitoringBroadcaster
{
    public Task AlertChangedAsync(AlertResponse alert, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task DeviceStatusChangedAsync(DeviceStatusTile tile, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

public interface IMonitoringLiveUpdateService
{
    /// <summary>
    /// Pushes an alert and the tile of the device it is on. Both, because one alert changing is
    /// exactly what makes a status tile change colour, and a board that had to poll for the second
    /// half would be a board that flickers.
    /// </summary>
    Task PublishAlertChangeAsync(Guid alertId, CancellationToken cancellationToken);
}

/// <summary>
/// Composes the payloads and hands them to the broadcaster. It swallows its own failures: a browser
/// that missed a push is a stale screen somebody can refresh, while an alert that failed to be raised
/// because a websocket was unhappy is an outage nobody hears about.
/// </summary>
public sealed class MonitoringLiveUpdateService(
    MonitoringDbContext dbContext,
    IAlertService alertService,
    IStatusBoardService statusBoardService,
    IMonitoringBroadcaster broadcaster,
    ILogger<MonitoringLiveUpdateService> logger) : IMonitoringLiveUpdateService
{
    public async Task PublishAlertChangeAsync(Guid alertId, CancellationToken cancellationToken)
    {
        try
        {
            var alert = await alertService.GetAsync(alertId, cancellationToken);
            if (alert is null)
            {
                return;
            }

            await broadcaster.AlertChangedAsync(alert.Alert, cancellationToken);

            var deviceId = await dbContext.Alerts.AsNoTracking()
                .Where(row => row.Id == alertId)
                .Select(row => row.DeviceId)
                .SingleOrDefaultAsync(cancellationToken);
            if (deviceId == Guid.Empty)
            {
                return;
            }

            if (await statusBoardService.GetTileAsync(deviceId, cancellationToken) is { } tile)
            {
                await broadcaster.DeviceStatusChangedAsync(tile, cancellationToken);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception,
                "Broadcasting alert {AlertId} to the dashboards failed; the boards will catch up on their next read.",
                alertId);
        }
    }
}
