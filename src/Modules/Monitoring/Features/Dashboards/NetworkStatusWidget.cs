using System.Security.Claims;

using Platform.Actors;
using Platform.Dashboards;

namespace Modules.Monitoring.Features.Dashboards;

/// <summary>
/// The estate's health in one tally (WP-5.5): how many devices are critical, warning, healthy, silent or
/// switched off.
/// <para>
/// It reads <see cref="IStatusBoardService.CountAsync"/> rather than counting for itself, so the number on
/// the dashboard and the number on the status board are the same number. They are one click apart, and two
/// counts that disagreed about how much of the estate is down would make both unusable.
/// </para>
/// </summary>
public sealed class NetworkStatusWidget(IStatusBoardService statusBoard) : IDashboardWidget
{
    public DashboardWidgetType Type => DashboardWidgetType.NetworkStatus;

    public string Title => "Network status";

    public bool IsVisibleTo(ClaimsPrincipal actor) => ActorRoles.IsAgent(actor);

    public async Task<DashboardWidgetData> LoadAsync(
        DashboardWidgetQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var counts = await statusBoard.CountAsync(cancellationToken);

        // Unknown and Disabled are drawn as well as the three that colour a tile, because leaving them out
        // would make the bands fail to add up to the device count above them — and a device that has never
        // reported is exactly the one somebody needs to notice. WP-3.9's rule that "never polled" is not
        // "healthy", restated one screen up.
        var segments = new List<DashboardSegment>
        {
            new("Critical", counts.Critical, DashboardTone.Critical,
                new DashboardLink(DashboardLinkTarget.DeviceList, nameof(DeviceStatus.Critical))),
            new("Warning", counts.Warning, DashboardTone.Warning,
                new DashboardLink(DashboardLinkTarget.DeviceList, nameof(DeviceStatus.Warning))),
            new("Healthy", counts.Ok, DashboardTone.Ok,
                new DashboardLink(DashboardLinkTarget.DeviceList, nameof(DeviceStatus.Ok))),
        };

        if (counts.Unknown > 0)
        {
            segments.Add(new DashboardSegment("Not yet reported", counts.Unknown, DashboardTone.Neutral,
                new DashboardLink(DashboardLinkTarget.DeviceList, nameof(DeviceStatus.Unknown))));
        }

        if (counts.Disabled > 0)
        {
            segments.Add(new DashboardSegment("Disabled", counts.Disabled, DashboardTone.Neutral,
                new DashboardLink(DashboardLinkTarget.DeviceList, nameof(DeviceStatus.Disabled))));
        }

        return new DashboardWidgetData(
            counts.Devices == 0
                ? "Nothing is monitored yet."
                : $"{counts.Devices} monitored device{(counts.Devices == 1 ? "" : "s")}",
            counts.Critical,
            "Critical now",
            segments,
            // No rows: which devices are down is the status board's whole screen, and a five-row copy of it
            // here would be a second, staler answer to a question one click away.
            [],
            0,
            new DashboardLink(DashboardLinkTarget.DeviceList),
            counts.Critical > 0 ? DashboardTone.Critical
                : counts.Warning > 0 ? DashboardTone.Warning
                : DashboardTone.Ok);
    }
}
