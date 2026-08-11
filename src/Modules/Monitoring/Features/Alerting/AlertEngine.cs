using System.Security.Claims;

using Contracts.Events;

using MassTransit;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Modules.Monitoring.Data;
using Modules.Monitoring.Features.Dashboards;

using Platform.Auditing;

namespace Modules.Monitoring.Features.Alerting;

public interface IAlertEngine
{
    /// <summary>
    /// Runs every rule the batch has a reading for. Returns how many alerts were raised or cleared —
    /// which is normally zero, because most cycles say nothing has changed.
    /// </summary>
    Task<int> EvaluateAsync(DeviceTelemetryReported telemetry, CancellationToken cancellationToken);
}

/// <summary>
/// Everything impure about alerting: which checks exist, which devices are inside a maintenance
/// window, what the durable alert rows say, and where the events go. The decisions themselves belong
/// to <see cref="AlertStateMachine"/>, <see cref="ThresholdEvaluator"/> and <see cref="AlertRules"/>,
/// which is why none of them can see any of this.
/// </summary>
public sealed class AlertEngine(
    MonitoringDbContext dbContext,
    IAlertStateStore stateStore,
    IAlertEnrichmentService enrichmentService,
    IMonitoringLiveUpdateService liveUpdates,
    IPublishEndpoint publishEndpoint,
    IAuditService auditService,
    IOptions<AlertOptions> options,
    ILogger<AlertEngine> logger) : IAlertEngine
{
    private static readonly ClaimsPrincipal SystemActor = new(new ClaimsIdentity(
        [new Claim("sub", "system:monitoring")],
        "Monitoring"));

    public async Task<int> EvaluateAsync(DeviceTelemetryReported telemetry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(telemetry);
        if (telemetry.Results.Count == 0)
        {
            return 0;
        }

        var checkIds = telemetry.Results.Select(result => result.CheckId).Distinct().ToList();
        var deviceIds = telemetry.Results.Select(result => result.DeviceId).Distinct().ToList();

        var checks = await dbContext.CheckDefinitions
            .Where(check => checkIds.Contains(check.Id))
            .ToDictionaryAsync(check => check.Id, cancellationToken);
        var muted = await MutedDevicesAsync(deviceIds, telemetry.OccurredAt, cancellationToken);
        var open = await dbContext.Alerts
            .Where(alert => deviceIds.Contains(alert.DeviceId) && alert.Status == AlertStatus.Open)
            .ToDictionaryAsync(alert => (alert.DeviceId, alert.RuleId), cancellationToken);

        var published = new List<PendingPublication>();

        // Oldest first. A batch is one cycle so the readings are near-simultaneous, but a poller that
        // fell behind can carry several cycles of one check, and the N-cycle rule is only a count of
        // consecutive readings if they are counted in the order they were taken.
        foreach (var result in telemetry.Results.OrderBy(result => result.ObservedAt))
        {
            if (!checks.TryGetValue(result.CheckId, out var check))
            {
                // Deleted between the poll and its ingestion. Its rules go with it; the alerts it
                // already raised are cascaded away by the same delete.
                logger.LogDebug("Telemetry for unknown check {CheckId} ignored by the alert engine.", result.CheckId);
                continue;
            }

            if (!check.IsEnabled)
            {
                continue;
            }

            var policy = AlertPolicy.Resolve(options.Value, check);
            var rules = AlertRules.RuleIds(check);
            var state = await LoadStateAsync(result.DeviceId, rules, open, cancellationToken);

            foreach (var observation in AlertRules.Observe(result, check, rules, policy, state))
            {
                var current = state[observation.RuleId];
                var transition = AlertStateMachine.Advance(
                    current,
                    observation.Severity,
                    observation.ObservedAt,
                    observation.Value,
                    policy,
                    muted.Contains(result.DeviceId),
                    Guid.CreateVersion7());

                var record = Apply(observation, transition, open, telemetry.PollerName);
                await stateStore.WriteAsync(
                    result.DeviceId, observation.RuleId, transition.State, cancellationToken);

                if (transition.Action is not AlertAction.None && record is not null)
                {
                    published.Add(new PendingPublication(observation, transition, record));
                }
            }
        }

        // The durable record is committed before anything is published, following WP-3.2's rule that
        // a failed publish should leave a row saying what happened rather than a message about an
        // alert nothing remembers.
        await dbContext.SaveChangesAsync(cancellationToken);

        foreach (var pending in published)
        {
            await PublishAsync(pending, cancellationToken);
        }

        return published.Count;
    }

    /// <summary>
    /// State for both of a check's rules, with anything Redis has forgotten rebuilt from the open
    /// alert row. This is what stops a Redis flush re-raising every alert in the estate: the counters
    /// and the flap history start again, but "this rule is already alerting, and it was published"
    /// comes from Postgres.
    /// </summary>
    private async Task<Dictionary<string, AlertState>> LoadStateAsync(
        Guid deviceId,
        CheckRuleIds rules,
        IReadOnlyDictionary<(Guid DeviceId, string RuleId), Alert> open,
        CancellationToken cancellationToken)
    {
        var state = new Dictionary<string, AlertState>(StringComparer.Ordinal);
        foreach (var ruleId in new[] { rules.Availability, rules.Threshold })
        {
            if (ruleId is null)
            {
                continue;
            }

            var stored = await stateStore.ReadAsync(deviceId, ruleId, cancellationToken);
            if (stored.AlertId is null && open.TryGetValue((deviceId, ruleId), out var alert))
            {
                stored = stored with
                {
                    AlertId = alert.Id,
                    Severity = alert.Severity,
                    // An alert whose row records no suppression is one somebody was told about.
                    PublishedSeverity = alert.Suppression is AlertSuppression.None
                        ? alert.Severity
                        : AlertSeverity.Ok,
                    RaisedAt = alert.RaisedAt,
                    ConsecutiveBreaches = alert.ConsecutiveBreaches,
                };
            }

            state[ruleId] = stored;
        }

        return state;
    }

    /// <summary>
    /// Reconciles the durable row with what the state machine now believes, and reports the facts a
    /// publication needs — the alert's id, when it was raised and what it cleared from. They are
    /// captured here rather than read back later because the row is written down to Ok on a clear and
    /// committed before anything is published, so by then it no longer remembers.
    /// </summary>
    private AlertRecord? Apply(
        AlertObservation observation,
        AlertTransition transition,
        Dictionary<(Guid DeviceId, string RuleId), Alert> open,
        string pollerName)
    {
        var key = (observation.DeviceId, observation.RuleId);
        open.TryGetValue(key, out var alert);

        if (transition.State.AlertId is { } alertId)
        {
            if (alert is null)
            {
                alert = new Alert
                {
                    Id = alertId,
                    DeviceId = observation.DeviceId,
                    CiId = observation.CiId,
                    CheckId = observation.CheckId,
                    RuleId = observation.RuleId,
                    MetricName = observation.MetricName,
                    Status = AlertStatus.Open,
                    RaisedAt = transition.State.RaisedAt ?? observation.ObservedAt,
                    Summary = observation.Summary,
                    PollerName = pollerName,
                };
                dbContext.Alerts.Add(alert);
                open[key] = alert;
            }

            alert.Severity = transition.Severity;
            alert.Summary = observation.Summary;
            alert.LastValue = observation.Value;
            alert.Threshold = observation.Threshold;
            alert.LastObservedAt = observation.ObservedAt;
            alert.ConsecutiveBreaches = transition.State.ConsecutiveBreaches;
            alert.IsFlapping = transition.State.IsFlapping(observation.ObservedAt);
            alert.Suppression = transition.SuppressedBy;
            return new AlertRecord(alert.Id, alert.RaisedAt, transition.Severity);
        }

        if (alert is null)
        {
            return null;
        }

        var previousSeverity = alert.Severity;
        alert.Status = AlertStatus.Cleared;
        alert.Severity = AlertSeverity.Ok;
        alert.Summary = observation.Summary;
        alert.LastValue = observation.Value;
        alert.LastObservedAt = observation.ObservedAt;
        alert.ClearedAt = observation.ObservedAt;
        alert.IsFlapping = transition.State.IsFlapping(observation.ObservedAt);
        alert.Suppression = AlertSuppression.None;
        open.Remove(key);
        return new AlertRecord(alert.Id, alert.RaisedAt, previousSeverity);
    }

    private async Task PublishAsync(PendingPublication pending, CancellationToken cancellationToken)
    {
        var (observation, transition, record) = pending;
        var alertId = record.AlertId;

        // WP-3.7: the CMDB context, read live for this publication only. It costs two port reads per
        // *published* alert — publications are rare by construction (a severity change, never a cycle),
        // so this is not on the evaluation hot path.
        var context = await enrichmentService.DescribeAsync(observation.CiId, cancellationToken);

        if (transition.Action is AlertAction.Raise)
        {
            logger.LogWarning(
                "Alert {Severity} raised on device {DeviceId} rule {RuleId}: {Summary} [{CmdbContext}]",
                transition.Severity, observation.DeviceId, observation.RuleId, observation.Summary,
                context.Headline);

            await publishEndpoint.Publish(
                new AlertRaised(
                    Guid.CreateVersion7(),
                    observation.ObservedAt,
                    alertId,
                    observation.DeviceId,
                    observation.CiId,
                    observation.CheckId,
                    observation.RuleId,
                    observation.CheckName,
                    transition.Severity.ToString(),
                    observation.MetricName,
                    observation.Value,
                    observation.Threshold,
                    observation.Summary,
                    record.RaisedAt,
                    transition.State.ConsecutiveBreaches),
                cancellationToken);
        }
        else
        {
            var raisedAt = record.RaisedAt;
            logger.LogInformation(
                "Alert cleared on device {DeviceId} rule {RuleId}. [{CmdbContext}]",
                observation.DeviceId, observation.RuleId, context.Headline);

            await publishEndpoint.Publish(
                new AlertCleared(
                    Guid.CreateVersion7(),
                    observation.ObservedAt,
                    alertId,
                    observation.DeviceId,
                    observation.CiId,
                    observation.CheckId,
                    observation.RuleId,
                    observation.CheckName,
                    record.PreviousSeverity.ToString(),
                    observation.MetricName,
                    observation.Value,
                    observation.Summary,
                    raisedAt,
                    (long)(observation.ObservedAt - raisedAt).TotalSeconds),
                cancellationToken);
        }

        // Also the flush: the outbox lives on the Platform context and the audit write is what commits
        // it, exactly as in WP-3.2's heartbeat evaluator. An alert is a fact an operator will want
        // dated, and this is the only place either event is recorded outside the alert row itself.
        await auditService.WriteAsync(
            SystemActor,
            transition.Action is AlertAction.Raise ? "AlertRaised" : "AlertCleared",
            "Alert",
            alertId.ToString(),
            before: null,
            after: new
            {
                observation.DeviceId,
                observation.RuleId,
                observation.MetricName,
                Severity = transition.Severity.ToString(),
                observation.Value,
                observation.Threshold,
                observation.Summary,
                // The CMDB context travels into the audit entry because that is the durable, dated
                // record of what the estate looked like when this fired — the alert row itself reads
                // its CI live and will answer differently once somebody reassigns the asset.
                Cmdb = context,
            },
            cancellationToken);

        // WP-3.9. The boards are told from here rather than from a consumer of the events above,
        // because the outbox delivers on its own sweep and a dashboard that lagged a durable queue
        // would not be live. This is safe to do at this point and not before: the alert row was
        // committed by the caller's SaveChangesAsync, so a browser can never be shown an alert the
        // database does not hold. It is also the only push that matters for latency — a broadcast is
        // a projection of committed state, and a browser that misses one re-reads on reconnect.
        await liveUpdates.PublishAlertChangeAsync(alertId, cancellationToken);
    }

    /// <summary>
    /// Devices an operator has said will be disturbed. A window mutes rather than pauses: the rules
    /// still run and the alert rows still appear, so what happened during a change is legible
    /// afterwards — only the events are withheld.
    /// </summary>
    private async Task<HashSet<Guid>> MutedDevicesAsync(
        IReadOnlyList<Guid> deviceIds,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var active = await dbContext.MaintenanceWindows
            .Where(window => window.IsActive && window.StartsAt <= now && window.EndsAt > now)
            .Select(window => new
            {
                window.AppliesToAllDevices,
                DeviceIds = window.Devices.Select(scope => scope.DeviceId).ToList(),
            })
            .ToListAsync(cancellationToken);

        if (active.Count == 0)
        {
            return [];
        }

        if (active.Any(window => window.AppliesToAllDevices))
        {
            return [.. deviceIds];
        }

        return [.. active.SelectMany(window => window.DeviceIds).Where(deviceIds.Contains)];
    }

    /// <summary>What the durable row said at the moment it was written, captured for the publication.</summary>
    private sealed record AlertRecord(Guid AlertId, DateTimeOffset RaisedAt, AlertSeverity PreviousSeverity);

    private sealed record PendingPublication(
        AlertObservation Observation,
        AlertTransition Transition,
        AlertRecord Record);
}
