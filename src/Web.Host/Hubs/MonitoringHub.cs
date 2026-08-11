using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

using Modules.Monitoring.Features.Alerting;
using Modules.Monitoring.Features.Dashboards;

namespace Web.Host.Hubs;

/// <summary>
/// The live half of the monitoring dashboards. Hubs live in the host (ARCHITECTURE §2) and the
/// module reaches this through <see cref="IMonitoringBroadcaster"/>, so nothing in
/// <c>Modules.Monitoring</c> knows SignalR exists.
/// <para>
/// The hub carries no methods a client can call. Everything a browser can do to an alert is an HTTP
/// endpoint that validates, authorizes and audits — acknowledging over a socket would be a second
/// write path around all three.
/// </para>
/// </summary>
[Authorize("CanManageMonitoring")]
public sealed class MonitoringHub : Hub<IMonitoringClient>;

/// <summary>
/// What the browser is sent. Named rather than stringly-typed so a renamed payload breaks the build
/// on this side and the SPA's client is the only other place the names appear.
/// </summary>
public interface IMonitoringClient
{
    Task AlertChanged(AlertResponse alert);

    Task DeviceStatusChanged(DeviceStatusTile tile);
}

/// <summary>
/// Sends to every connected operator. There are no groups and no per-device subscriptions: the
/// audience is the set of people allowed to see the monitoring surface at all, both boards want
/// everything, and a device page filters the one device it is showing in the browser.
/// </summary>
public sealed class SignalRMonitoringBroadcaster(IHubContext<MonitoringHub, IMonitoringClient> hub)
    : IMonitoringBroadcaster
{
    public Task AlertChangedAsync(AlertResponse alert, CancellationToken cancellationToken) =>
        hub.Clients.All.AlertChanged(alert);

    public Task DeviceStatusChangedAsync(DeviceStatusTile tile, CancellationToken cancellationToken) =>
        hub.Clients.All.DeviceStatusChanged(tile);
}
