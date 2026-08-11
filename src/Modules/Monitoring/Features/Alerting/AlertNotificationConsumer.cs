using Contracts.Events;

using MassTransit;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Modules.Monitoring.Data;

using Platform.Data;
using Platform.Messaging;
using Platform.Notifications;

namespace Modules.Monitoring.Features.Alerting;

public interface IAlertNotificationService
{
    Task NotifyRaisedAsync(AlertRaised alert, CancellationToken cancellationToken);

    Task NotifyClearedAsync(AlertCleared alert, CancellationToken cancellationToken);
}

/// <summary>
/// Turns an alert into a notification and hands it to Platform's router (WP-3.10).
/// <para>
/// It lives in Monitoring rather than in Platform for one reason: routing rules can be scoped to a
/// <em>device group</em>, and the poller group is a monitoring fact. Platform may not read a
/// monitoring table, so the group travels on the envelope — resolved here, beside the CMDB context
/// WP-3.7 already assembles for the same alert.
/// </para>
/// </summary>
public sealed class AlertNotificationService(
    MonitoringDbContext dbContext,
    IAlertEnrichmentService enrichmentService,
    INotificationRouter router,
    IOptions<NotificationOptions> notificationOptions,
    ILogger<AlertNotificationService> logger) : IAlertNotificationService
{
    public Task NotifyRaisedAsync(AlertRaised alert, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(alert);
        return NotifyAsync(
            nameof(AlertRaised),
            Severity(alert.Severity),
            // The poller writes whole sentences that already name the check — "Reachability on
            // snmpsim is failing: …" — so prefixing the check name produced "Reachability:
            // Reachability on snmpsim…" on every message in the estate. Found by this package's own
            // hand-verification, the same way WP-3.8 found the double full stop.
            $"[{alert.Severity}] {alert.Summary}",
            alert.Summary,
            alert.AlertId,
            alert.DeviceId,
            alert.CiId,
            // Keyed on the rule and the device rather than the alert row, matching WP-3.6's durable
            // dedupe key. An alert row is new on every recurrence, so keying on it meant the digest
            // could never collapse "this rule failed eleven times overnight" into one line — which is
            // the whole point of a digest.
            $"alert:{alert.DeviceId}:{alert.RuleId}:raised",
            extra:
            [
                new NotificationFact("Check", alert.CheckName),
                new NotificationFact("Rule", alert.RuleId),
                new NotificationFact("Metric", alert.MetricName),
                new NotificationFact("Value", Format(alert.Value)),
                new NotificationFact("Threshold", Format(alert.Threshold)),
                new NotificationFact("Breaches", alert.ConsecutiveBreaches.ToString()),
                new NotificationFact("Raised at", $"{alert.RaisedAt:u}"),
            ],
            cancellationToken);
    }

    public Task NotifyClearedAsync(AlertCleared alert, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(alert);
        return NotifyAsync(
            nameof(AlertCleared),
            // A recovery is news, not an emergency. It is Informational so that a rule set to
            // "Critical only" stays a pager and does not also become the all-clear.
            NotificationSeverity.Informational,
            $"[Cleared] {alert.Summary}",
            alert.Summary,
            alert.AlertId,
            alert.DeviceId,
            alert.CiId,
            $"alert:{alert.DeviceId}:{alert.RuleId}:cleared",
            extra:
            [
                new NotificationFact("Check", alert.CheckName),
                new NotificationFact("Rule", alert.RuleId),
                new NotificationFact("Was", alert.PreviousSeverity),
                new NotificationFact("Open for", $"{TimeSpan.FromSeconds(alert.DurationSeconds):g}"),
            ],
            cancellationToken);
    }

    private async Task NotifyAsync(
        string eventKind,
        NotificationSeverity severity,
        string subject,
        string body,
        Guid alertId,
        Guid deviceId,
        Guid ciId,
        string dedupeKey,
        IReadOnlyList<NotificationFact> extra,
        CancellationToken cancellationToken)
    {
        var device = await dbContext.MonitoredDevices.AsNoTracking()
            .Where(item => item.Id == deviceId)
            .Select(item => new { item.PollerGroup, item.Address })
            .SingleOrDefaultAsync(cancellationToken);
        if (device is null)
        {
            // The alert outlived its device, which nothing prevents. Still worth sending — it just
            // matches no rule that names a group.
            logger.LogWarning("Alert {AlertId} names device {DeviceId}, which no longer exists.", alertId, deviceId);
        }

        var context = await enrichmentService.DescribeAsync(ciId, cancellationToken);
        var facts = new List<NotificationFact>
        {
            new("Asset", context.CiName ?? "not found in the CMDB"),
            new("Owner", context.OwnerName ?? "none"),
            new("Location", context.SiteName ?? "none"),
            new("Warranty", context.WarrantyStatus ?? "none recorded"),
            new("Open tickets", context.OpenTickets.Count.ToString()),
            new("Device group", device?.PollerGroup ?? "unknown"),
            new("Address", device?.Address ?? "unknown"),
        };
        facts.AddRange(extra);

        var envelope = new NotificationEnvelope(
            eventKind,
            severity,
            subject,
            body,
            DeepLink(alertId),
            device?.PollerGroup,
            dedupeKey,
            facts);

        // No user is named: an alert is about an asset, and the CMDB port answers with an owner's
        // *name* rather than an id (WP-2.4), so there is nobody here to address personally. Alerts
        // therefore reach channels only; per-user preferences govern the SLA path, which does hold an
        // identity. Widening `ICiDirectory` to carry an owner id is the change that would fix it.
        var report = await router.RouteAsync(envelope, userIds: null, cancellationToken);
        logger.LogInformation(
            "Alert {AlertId} notification routed: {Sent} sent, {Deferred} deferred, {Suppressed} suppressed, {Failed} failed.",
            alertId, report.Sent, report.Deferred, report.Suppressed, report.Failed);
    }

    /// <summary>
    /// The alert board, filtered to the one alert — the SPA opens its detail drawer from the query
    /// string. Absolute, because the link is read on a phone in a chat client that has no idea what
    /// host wrote it; the base follows the WP-2.7 label-URL rule.
    /// </summary>
    private string? DeepLink(Guid alertId)
    {
        var baseUrl = notificationOptions.Value.DeepLinkBaseUrl;
        return string.IsNullOrWhiteSpace(baseUrl)
            ? null
            : $"{baseUrl.TrimEnd('/')}/monitoring/alerts?alertId={alertId}";
    }

    /// <summary>
    /// Monitoring's severity in Platform's terms. Deliberately a translation rather than a shared
    /// enum: <c>AlertSeverity</c> belongs to this module, and Platform may not reference it.
    /// </summary>
    public static NotificationSeverity Severity(string alertSeverity) =>
        Enum.TryParse<AlertSeverity>(alertSeverity, ignoreCase: true, out var parsed) && parsed is AlertSeverity.Critical
            ? NotificationSeverity.Critical
            : NotificationSeverity.Warning;

    private static string Format(double? value) =>
        value is null ? "—" : value.Value.ToString("0.###");
}

/// <summary>
/// Deduped through the Platform helper: a redelivered message would otherwise post the same alert into
/// the same Teams channel twice, and a notification nobody can un-send is exactly the kind of
/// double-entry the helper exists for.
/// </summary>
public sealed class AlertRaisedNotificationConsumer(
    IConsumerIdempotencyService idempotencyService,
    IAlertNotificationService notificationService,
    ILogger<AlertRaisedNotificationConsumer> logger) : IConsumer<AlertRaised>
{
    public async Task Consume(ConsumeContext<AlertRaised> context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var alert = context.Message;
        var accepted = await idempotencyService.ExecuteOnceAsync(
            $"alert-notification-raised:{alert.EventId}",
            cancellationToken => notificationService.NotifyRaisedAsync(alert, cancellationToken),
            context.CancellationToken);
        if (!accepted)
        {
            logger.LogDebug("AlertRaised {EventId} was already notified; skipped.", alert.EventId);
        }
    }
}

public sealed class AlertClearedNotificationConsumer(
    IConsumerIdempotencyService idempotencyService,
    IAlertNotificationService notificationService,
    ILogger<AlertClearedNotificationConsumer> logger) : IConsumer<AlertCleared>
{
    public async Task Consume(ConsumeContext<AlertCleared> context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var alert = context.Message;
        var accepted = await idempotencyService.ExecuteOnceAsync(
            $"alert-notification-cleared:{alert.EventId}",
            cancellationToken => notificationService.NotifyClearedAsync(alert, cancellationToken),
            context.CancellationToken);
        if (!accepted)
        {
            logger.LogDebug("AlertCleared {EventId} was already notified; skipped.", alert.EventId);
        }
    }
}
