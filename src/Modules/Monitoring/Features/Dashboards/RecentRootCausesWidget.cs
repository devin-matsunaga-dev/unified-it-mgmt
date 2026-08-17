using System.Security.Claims;

using Microsoft.EntityFrameworkCore;

using Modules.Monitoring.Data;

using Platform.Actors;
using Platform.Dashboards;

namespace Modules.Monitoring.Features.Dashboards;

/// <summary>
/// The alerts that explained other alerts (WP-5.5) — WP-5.1's correlation, read as a list rather than as a
/// badge on a board.
/// <para>
/// A root cause is defined here by what points at it: an alert is on this list when at least one other
/// alert names it as its cause. That is the only definition the data supports, and it is the one that
/// matters — an alert nothing was filed under explained nothing, however severe it was.
/// </para>
/// </summary>
public sealed class RecentRootCausesWidget(MonitoringDbContext dbContext) : IDashboardWidget
{
    /// <summary>
    /// How far back the list looks. A week rather than "ever": the card answers "what has been breaking
    /// things lately", and an unbounded list would be led forever by whichever outage was worst in the
    /// platform's history.
    /// </summary>
    public static TimeSpan Window { get; } = TimeSpan.FromDays(7);

    public DashboardWidgetType Type => DashboardWidgetType.RecentRootCauses;

    public string Title => "Recent root causes";

    public bool IsVisibleTo(ClaimsPrincipal actor) => ActorRoles.IsAgent(actor);

    public async Task<DashboardWidgetData> LoadAsync(
        DashboardWidgetQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var since = DateTimeOffset.UtcNow - Window;
        var causes = dbContext.Alerts.AsNoTracking()
            .Where(alert => alert.RaisedAt >= since
                && dbContext.Alerts.Any(other => other.RootCauseAlertId == alert.Id));

        var total = await causes.CountAsync(cancellationToken);
        var rows = await causes
            // Newest first, not worst first: this is a history of what has happened, and the board next
            // door is where what is worst right now is read.
            .OrderByDescending(alert => alert.RaisedAt)
            .ThenBy(alert => alert.Id)
            .Take(query.RowLimit)
            .Select(alert => new
            {
                alert.Id,
                alert.Summary,
                alert.Severity,
                alert.Status,
                alert.RaisedAt,
                // The address, because a monitored device has no name of its own — it borrows the CI's, and
                // that lives in a schema this module may not join to (WP-5.3 and WP-5.4 both said so).
                Address = alert.Device.Address,
                Impacted = dbContext.Alerts.Count(other => other.RootCauseAlertId == alert.Id),
            })
            .ToListAsync(cancellationToken);

        var openCount = await causes.CountAsync(alert => alert.Status == AlertStatus.Open, cancellationToken);

        return new DashboardWidgetData(
            total == 0
                ? $"Nothing has explained another alert in the last {Window.TotalDays:0} days."
                : $"In the last {Window.TotalDays:0} days",
            openCount,
            "Still open",
            [],
            [
                .. rows.Select(row => new DashboardRow(
                    row.Summary,
                    $"{row.Address} · explains {row.Impacted} alert{(row.Impacted == 1 ? "" : "s")}",
                    // A cleared cause is labelled as recovered rather than by its severity: the severity of
                    // something that is over is a fact about the past, and reading "Critical" beside a
                    // recovered outage is how somebody chases a problem that has already gone away.
                    row.Status == AlertStatus.Cleared ? "Recovered" : row.Severity.ToString(),
                    row.Status == AlertStatus.Cleared
                        ? DashboardTone.Neutral
                        : row.Severity == AlertSeverity.Critical ? DashboardTone.Critical : DashboardTone.Warning,
                    new DashboardLink(DashboardLinkTarget.Alert, RecordId: row.Id),
                    row.RaisedAt)),
            ],
            total,
            new DashboardLink(DashboardLinkTarget.AlertList),
            // An outage that is still open is worth a colour; one that has been explained and recovered is
            // history, and history is read in grey.
            openCount > 0 ? DashboardTone.Warning : DashboardTone.Neutral);
    }
}
