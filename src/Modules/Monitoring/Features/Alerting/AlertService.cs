using System.Security.Claims;

using Microsoft.EntityFrameworkCore;

using Modules.Monitoring.Data;

using Platform.Auditing;

namespace Modules.Monitoring.Features.Alerting;

public interface IAlertService
{
    Task<AlertPageResponse> ListAsync(AlertListRequest request, CancellationToken cancellationToken);

    Task<AlertDetailResponse?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<AlertActionResult> AcknowledgeAsync(Guid id, ClaimsPrincipal actor, CancellationToken cancellationToken);
}

/// <summary>
/// The read surface WP-3.5 and WP-3.7 both deliberately left to this package, plus the one write it
/// has: an acknowledgement. Everything here reads the durable <c>monitoring.alerts</c> row — never the
/// Redis state, which is the state machine's working memory and not a source of truth (ARCHITECTURE §5).
/// </summary>
public sealed class AlertService(
    MonitoringDbContext dbContext,
    IAlertEnrichmentService enrichmentService,
    IAuditService auditService) : IAlertService
{
    private const int MaximumPageSize = 200;

    public async Task<AlertPageResponse> ListAsync(
        AlertListRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var page = Math.Max(request.Page, 1);
        var pageSize = Math.Clamp(request.PageSize, 1, MaximumPageSize);

        var query = dbContext.Alerts.AsNoTracking();
        if (request.Status is { } status)
        {
            query = query.Where(alert => alert.Status == status);
        }

        if (request.Severity is { } severity)
        {
            query = query.Where(alert => alert.Severity == severity);
        }

        if (request.DeviceId is { } deviceId)
        {
            query = query.Where(alert => alert.DeviceId == deviceId);
        }

        if (request.CiId is { } ciId)
        {
            query = query.Where(alert => alert.CiId == ciId);
        }

        if (request.Acknowledged is { } acknowledged)
        {
            query = acknowledged
                ? query.Where(alert => alert.AcknowledgedAt != null)
                : query.Where(alert => alert.AcknowledgedAt == null);
        }

        var total = await query.CountAsync(cancellationToken);
        var rows = await Ordered(query)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(alert => new AlertRow(
                alert,
                alert.Device.Address,
                dbContext.CheckDefinitions
                    .Where(check => check.Id == alert.CheckId)
                    .Select(check => check.Name)
                    .FirstOrDefault()))
            .ToListAsync(cancellationToken);

        var summaries = await enrichmentService.SummariseAsync(
            [.. rows.Select(row => row.Alert.CiId)], cancellationToken);

        return new AlertPageResponse(
            [.. rows.Select(row => Map(row, summaries))],
            total,
            page,
            pageSize,
            await CountAsync(cancellationToken));
    }

    public async Task<AlertDetailResponse?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var row = await dbContext.Alerts.AsNoTracking()
            .Where(alert => alert.Id == id)
            .Select(alert => new AlertRow(
                alert,
                alert.Device.Address,
                dbContext.CheckDefinitions
                    .Where(check => check.Id == alert.CheckId)
                    .Select(check => check.Name)
                    .FirstOrDefault()))
            .SingleOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return null;
        }

        // The full WP-3.7 context here, open tickets included: this is one alert somebody opened, so
        // the extra port read is one query rather than one per row of a board.
        var context = await enrichmentService.DescribeAsync(row.Alert.CiId, cancellationToken);
        return new AlertDetailResponse(Map(row, context), context.OpenTickets);
    }

    public async Task<AlertActionResult> AcknowledgeAsync(
        Guid id,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var alert = await dbContext.Alerts.SingleOrDefaultAsync(row => row.Id == id, cancellationToken);
        if (alert is null)
        {
            return new AlertActionResult(AlertActionOutcome.NotFound);
        }

        // An acknowledgement says "I am dealing with this". There is nothing to deal with on an alert
        // that has already cleared, and a recurrence opens a new row rather than reviving this one, so
        // acknowledging history would only ever mislead the next person to read it.
        if (alert.Status is not AlertStatus.Open)
        {
            return new AlertActionResult(
                AlertActionOutcome.Conflict,
                Error: "This alert has already cleared, so there is nothing to acknowledge.");
        }

        if (alert.AcknowledgedAt is not null)
        {
            return new AlertActionResult(
                AlertActionOutcome.Conflict,
                Error: $"Already acknowledged by {alert.AcknowledgedByName ?? alert.AcknowledgedBy} "
                    + $"at {alert.AcknowledgedAt:u}.");
        }

        var before = new { alert.AcknowledgedAt, alert.AcknowledgedBy };
        alert.AcknowledgedAt = DateTimeOffset.UtcNow;
        alert.AcknowledgedBy = ActorId(actor);
        alert.AcknowledgedByName = ActorDisplayName(actor);
        await dbContext.SaveChangesAsync(cancellationToken);

        // Every write endpoint produces an audit entry (ARCHITECTURE §7.1). Who claimed an incident
        // and when is exactly the sort of thing an after-action review asks for.
        await auditService.WriteAsync(
            actor,
            "AlertAcknowledged",
            "Alert",
            alert.Id.ToString(),
            before,
            new { alert.AcknowledgedAt, alert.AcknowledgedBy, alert.AcknowledgedByName, alert.RuleId },
            cancellationToken);

        var detail = await GetAsync(alert.Id, cancellationToken);
        return new AlertActionResult(AlertActionOutcome.Success, detail?.Alert);
    }

    /// <summary>
    /// Worst first, then oldest. Severity is stored as its name, so ordering by the column would sort
    /// Critical, Ok, Warning alphabetically and bury the warnings below the recoveries; these two
    /// boolean keys are what makes Postgres order it by meaning instead.
    /// </summary>
    private static IQueryable<Alert> Ordered(IQueryable<Alert> query) => query
        .OrderByDescending(alert => alert.Severity == AlertSeverity.Critical)
        .ThenByDescending(alert => alert.Severity == AlertSeverity.Warning)
        .ThenByDescending(alert => alert.RaisedAt)
        .ThenBy(alert => alert.Id);

    private async Task<AlertCounts> CountAsync(CancellationToken cancellationToken)
    {
        // Counted over every open alert rather than over the page: a headline figure that changed
        // when somebody turned a page would be a headline figure nobody could quote.
        var open = dbContext.Alerts.AsNoTracking().Where(alert => alert.Status == AlertStatus.Open);
        return new AlertCounts(
            await open.CountAsync(cancellationToken),
            await open.CountAsync(alert => alert.Severity == AlertSeverity.Critical, cancellationToken),
            await open.CountAsync(alert => alert.Severity == AlertSeverity.Warning, cancellationToken),
            await open.CountAsync(alert => alert.AcknowledgedAt == null, cancellationToken));
    }

    private static AlertResponse Map(AlertRow row, IReadOnlyDictionary<Guid, AlertCmdbSummary> summaries) =>
        Map(row, summaries.TryGetValue(row.Alert.CiId, out var summary)
            ? summary
            : AlertCmdbSummary.NotFound(row.Alert.CiId));

    private static AlertResponse Map(AlertRow row, AlertCmdbContext context) => Map(
        row,
        new AlertCmdbSummary(
            context.CiId, context.CiFound, context.CiName, context.CiType, context.AssetTag,
            context.LifecycleState, context.OwnerName, context.SiteName, context.DepartmentName,
            context.WarrantyExpiresAt, context.WarrantyStatus, context.WarrantyDaysRemaining,
            context.ContractName));

    private static AlertResponse Map(AlertRow row, AlertCmdbSummary ci)
    {
        var alert = row.Alert;
        return new AlertResponse(
            alert.Id,
            alert.DeviceId,
            alert.CiId,
            alert.CheckId,
            alert.RuleId,
            alert.MetricName,
            alert.Severity,
            alert.Status,
            alert.Summary,
            alert.LastValue,
            alert.Threshold,
            alert.ConsecutiveBreaches,
            alert.IsFlapping,
            alert.Suppression,
            alert.RaisedAt,
            alert.LastObservedAt,
            alert.ClearedAt,
            alert.PollerName,
            alert.AcknowledgedAt,
            alert.AcknowledgedBy,
            alert.AcknowledgedByName,
            row.DeviceAddress,
            row.CheckName,
            ci.CiFound,
            ci.CiName,
            ci.CiType,
            ci.AssetTag,
            ci.LifecycleState,
            ci.OwnerName,
            ci.SiteName,
            ci.DepartmentName,
            ci.WarrantyExpiresAt,
            ci.WarrantyStatus,
            ci.WarrantyDaysRemaining,
            ci.ContractName);
    }

    private static string ActorId(ClaimsPrincipal actor) =>
        actor.FindFirstValue("sub") ?? actor.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException("An authenticated actor identifier is required.");

    private static string ActorDisplayName(ClaimsPrincipal actor) =>
        actor.FindFirstValue("name") ?? actor.Identity?.Name ?? actor.FindFirstValue("preferred_username")
        ?? ActorId(actor);

    /// <summary>
    /// The alert plus the two names that live on other rows. The check name is read here rather than
    /// snapshotted on the alert, following the same rule as the CI fields — renaming a check has to
    /// reach every alert it raised, and the check row cannot leave without cascading the alert away.
    /// </summary>
    private sealed record AlertRow(Alert Alert, string? DeviceAddress, string? CheckName);
}
