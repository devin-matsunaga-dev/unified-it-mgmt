using Microsoft.EntityFrameworkCore;

using Modules.Monitoring.Data;

using Platform.Integration;

namespace Modules.Monitoring.Features.Alerting;

/// <summary>
/// Monitoring's side of <see cref="IAlertCorrelationDirectory"/>: what a root-cause alert is currently
/// explaining. Read by Helpdesk while it composes the one ticket an outage produces, so that the
/// ticket can name the CIs that went with it.
/// <para>
/// Open alerts only. A consequence that has since recovered is not part of the incident somebody is
/// about to be handed — and the ticket's description is written once, so listing a device that came
/// back a minute ago would leave a permanent claim that it was affected.
/// </para>
/// </summary>
public sealed class AlertCorrelationDirectory(MonitoringDbContext dbContext) : IAlertCorrelationDirectory
{
    public async Task<IReadOnlyList<CorrelatedAlertSummary>> GetImpactedByAsync(
        Guid rootCauseAlertId,
        CancellationToken cancellationToken)
    {
        var rows = await dbContext.Alerts
            .Where(alert => alert.RootCauseAlertId == rootCauseAlertId && alert.Status == AlertStatus.Open)
            // Oldest first, which on an outage is roughly the order things fell over.
            .OrderBy(alert => alert.RaisedAt)
            .ThenBy(alert => alert.Id)
            .Select(alert => new
            {
                alert.Id,
                alert.CiId,
                alert.DeviceId,
                alert.RuleId,
                alert.Severity,
                alert.Summary,
            })
            .ToListAsync(cancellationToken);

        return
        [
            .. rows.Select(row => new CorrelatedAlertSummary(
                row.Id, row.CiId, row.DeviceId, row.RuleId, row.Severity.ToString(), row.Summary)),
        ];
    }
}
