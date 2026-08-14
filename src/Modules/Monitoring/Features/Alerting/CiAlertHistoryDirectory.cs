using Microsoft.EntityFrameworkCore;

using Modules.Monitoring.Data;

using Platform.Integration;

namespace Modules.Monitoring.Features.Alerting;

/// <summary>
/// Monitoring's side of <see cref="ICiAlertHistoryDirectory"/>: everything that has ever gone wrong on
/// one CI, for the timeline on its asset page (WP-5.3).
/// <para>
/// Cleared alerts included, which is the difference between this and every other alert read in the
/// module. The board answers "what is broken"; this answers "what has been", and a CI whose history is
/// only its currently-open alerts has no history at all.
/// </para>
/// <para>
/// Suppressed alerts are included too, and deliberately. WP-5.1 withholds a consequence's <em>event</em>
/// so nobody is paged twice for one outage — but the alert was real, it was recorded, and the timeline is
/// exactly where somebody asking "was this machine affected on Tuesday" should find it. What it carries
/// instead of hiding them is the suppression reason, so the row can say why nothing was sent.
/// </para>
/// </summary>
public sealed class CiAlertHistoryDirectory(MonitoringDbContext dbContext) : ICiAlertHistoryDirectory
{
    /// <summary>The most alerts one call will return, whatever the caller asks for.</summary>
    internal const int MaximumLimit = 200;

    public async Task<CiAlertHistory> GetAlertsForCiAsync(
        Guid ciId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        int limit,
        CancellationToken cancellationToken)
    {
        // Windowed on RaisedAt, which is the moment the alert takes on the axis. An alert raised before
        // the window and cleared inside it is therefore out of scope: the timeline is a record of when
        // things happened, and this one happened earlier.
        var matching = dbContext.Alerts
            .AsNoTracking()
            .Where(alert => alert.CiId == ciId);

        if (from is not null)
        {
            matching = matching.Where(alert => alert.RaisedAt >= from);
        }

        if (to is not null)
        {
            matching = matching.Where(alert => alert.RaisedAt <= to);
        }

        var total = await matching.CountAsync(cancellationToken);

        // Projected to an anonymous type and turned into the record in memory, following
        // <see cref="AlertCorrelationDirectory"/>: the enums are printed by name, and a `ToString()`
        // inside the projection is the kind of expression that compiles and then fails as a 500.
        var rows = await matching
            .OrderByDescending(alert => alert.RaisedAt)
            .ThenByDescending(alert => alert.Id)
            .Take(Math.Clamp(limit, 1, MaximumLimit))
            .Select(alert => new
            {
                alert.Id,
                alert.DeviceId,
                DeviceAddress = alert.Device.Address,
                alert.RuleId,
                alert.MetricName,
                alert.Severity,
                alert.Status,
                alert.Summary,
                alert.Suppression,
                alert.RaisedAt,
                alert.ClearedAt,
                alert.AcknowledgedAt,
                alert.AcknowledgedByName,
            })
            .ToListAsync(cancellationToken);

        return new(
            [
                .. rows.Select(row => new CiAlertHistoryEntry(
                    row.Id,
                    row.DeviceId,
                    row.DeviceAddress,
                    row.RuleId,
                    row.MetricName,
                    row.Severity.ToString(),
                    row.Status.ToString(),
                    row.Summary,
                    row.Suppression.ToString(),
                    row.RaisedAt,
                    row.ClearedAt,
                    row.AcknowledgedAt,
                    row.AcknowledgedByName)),
            ],
            total);
    }
}
