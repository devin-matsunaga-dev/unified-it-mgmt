using System.Security.Claims;

using Contracts.Events;

using MassTransit;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Modules.Monitoring.Data;
using Modules.Monitoring.Features.Dashboards;

using Platform.Auditing;
using Platform.Integration;

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
    ICiDependencyDirectory dependencyDirectory,
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

        // Estate-wide, and that is WP-5.1's change to this query: it used to load only the devices in
        // this batch. A cause and its consequences sit on different devices by definition, and are
        // routinely polled on different cycles by different pollers, so correlation cannot see the
        // failure it needs from a batch-shaped window. The cost is bounded by how much is broken
        // rather than by the size of the estate — an alert row exists only while something is wrong.
        var open = await dbContext.Alerts
            .Where(alert => alert.Status == AlertStatus.Open)
            .ToDictionaryAsync(alert => (alert.DeviceId, alert.RuleId), cancellationToken);

        // Evaluated in three passes rather than one, because whether an alert is worth publishing now
        // depends on what else is failing — which is not known until every rule in the batch has been
        // advanced. Pass one advances everything as though nothing were correlated; pass two works out
        // which failures explain which; pass three re-advances the explained ones, from the state they
        // started in, as suppressed. The state machine stays pure and is simply run twice for the few
        // rules whose answer changed.
        var candidates = await EvaluateCandidatesAsync(telemetry, checks, muted, open, cancellationToken);
        var untouched = UntouchedOpenAlerts(candidates, open);
        var correlation = await CorrelateAsync(candidates, untouched, cancellationToken);
        Recorrelate(candidates, correlation);

        var published = new List<PendingPublication>();
        foreach (var candidate in candidates)
        {
            var record = Apply(candidate.Observation, candidate.Transition, open, telemetry.PollerName, correlation);
            await stateStore.WriteAsync(
                candidate.Observation.DeviceId,
                candidate.Observation.RuleId,
                candidate.Transition.State,
                cancellationToken);

            if (candidate.Transition.Action is not AlertAction.None && record is not null)
            {
                published.Add(new PendingPublication(candidate.Observation, candidate.Transition, record));
            }
        }

        RegroupUntouchedAlerts(untouched, correlation);

        // The durable record is committed before anything is published, following WP-3.2's rule that
        // a failed publish should leave a row saying what happened rather than a message about an
        // alert nothing remembers. WP-5.1 leans on the same ordering for a second reason: the
        // root-cause ticket reads the suppressed alerts back through a port while handling the event
        // published below, so every one of them has to be committed before that event exists.
        await dbContext.SaveChangesAsync(cancellationToken);

        foreach (var pending in published)
        {
            await PublishAsync(pending, cancellationToken);
        }

        return published.Count;
    }

    /// <summary>
    /// Pass one: advance every rule the batch has a reading for, as though nothing were correlated,
    /// and keep what each one started from. Nothing is written here — not the alert rows, not Redis —
    /// because pass three may have to run some of these again from the state they began in.
    /// </summary>
    private async Task<List<Candidate>> EvaluateCandidatesAsync(
        DeviceTelemetryReported telemetry,
        IReadOnlyDictionary<Guid, CheckDefinition> checks,
        IReadOnlySet<Guid> muted,
        IReadOnlyDictionary<(Guid DeviceId, string RuleId), Alert> open,
        CancellationToken cancellationToken)
    {
        var candidates = new List<Candidate>();

        // The batch's own view of state, layered over Redis. It used to be Redis itself doing this
        // job, because the old loop wrote each rule's state before reading the next result. Deferring
        // those writes means a batch carrying several cycles of one check would otherwise read the
        // same starting state for each of them, and the "for N cycles" counter would never advance.
        var working = new Dictionary<(Guid DeviceId, string RuleId), AlertState>();

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
            // WP-4.5: an interface check's rules come from what it just reported rather than from
            // how it is configured, so they are derived per result and joined onto the check's own
            // two. Every other check type contributes an empty list here.
            var interfaceRules = InterfaceAlertRules.RuleIds(result, check);
            var state = await LoadStateAsync(
                result.DeviceId,
                [rules.Availability, rules.Threshold, .. interfaceRules],
                open,
                working,
                cancellationToken);

            var isMuted = muted.Contains(result.DeviceId);
            var observations = AlertRules.Observe(result, check, rules, policy, state)
                .Concat(InterfaceAlertRules.Observe(result, check, policy, state));
            foreach (var observation in observations)
            {
                var current = state[observation.RuleId];
                // Allocated once and carried, so that pass three re-running this rule produces the
                // same alert id rather than a second one. The state machine is a function of its
                // inputs and this is one of them.
                var newAlertId = Guid.CreateVersion7();
                var transition = AlertStateMachine.Advance(
                    current,
                    observation.Severity,
                    observation.ObservedAt,
                    observation.Value,
                    policy,
                    isMuted,
                    newAlertId);

                candidates.Add(new Candidate(observation, current, transition, policy, newAlertId, isMuted));
                working[(observation.DeviceId, observation.RuleId)] = transition.State;
            }
        }

        return candidates;
    }

    /// <summary>
    /// The open alerts this batch says nothing about. They still count toward what is failing — a
    /// switch that went down ten minutes ago is not in tonight's telemetry for the servers behind it —
    /// and they are the rows <see cref="RegroupUntouchedAlerts"/> re-files afterwards.
    /// </summary>
    private static List<Alert> UntouchedOpenAlerts(
        IReadOnlyList<Candidate> candidates,
        IReadOnlyDictionary<(Guid DeviceId, string RuleId), Alert> open)
    {
        var touched = candidates
            .Select(candidate => (candidate.Observation.DeviceId, candidate.Observation.RuleId))
            .ToHashSet();
        return [.. open.Values.Where(alert => !touched.Contains((alert.DeviceId, alert.RuleId)))];
    }

    /// <summary>
    /// Pass two: everything that will be failing once this batch is applied, and which of those
    /// failures explains which. The graph read is the only thing on this path that leaves the module,
    /// and it is skipped entirely unless at least two CIs are in trouble — one CI cannot be a
    /// consequence of itself, so the overwhelmingly common case costs nothing.
    /// </summary>
    private async Task<CorrelationOutcome> CorrelateAsync(
        IReadOnlyList<Candidate> candidates,
        IReadOnlyList<Alert> untouched,
        CancellationToken cancellationToken)
    {
        if (!options.Value.CorrelationEnabled)
        {
            return CorrelationOutcome.None;
        }

        var failingSince = new Dictionary<Guid, DateTimeOffset>();
        var alertsByCi = new Dictionary<Guid, List<OpenAlertFact>>();

        void Record(Guid ciId, Guid alertId, AlertSeverity severity, DateTimeOffset raisedAt)
        {
            // A device whose CI was deleted still alerts, and still deserves its ticket — it simply
            // cannot take part in a correlation, because it is not on the graph.
            if (ciId == Guid.Empty)
            {
                return;
            }

            if (!failingSince.TryGetValue(ciId, out var since) || raisedAt < since)
            {
                failingSince[ciId] = raisedAt;
            }

            if (!alertsByCi.TryGetValue(ciId, out var alerts))
            {
                alertsByCi[ciId] = alerts = [];
            }

            alerts.Add(new OpenAlertFact(alertId, severity, raisedAt));
        }

        foreach (var candidate in candidates)
        {
            if (candidate.Transition.Severity is AlertSeverity.Ok
                || candidate.Transition.State.AlertId is not { } alertId)
            {
                continue;
            }

            Record(
                candidate.Observation.CiId,
                alertId,
                candidate.Transition.Severity,
                candidate.Transition.State.RaisedAt ?? candidate.Observation.ObservedAt);
        }

        foreach (var alert in untouched.Where(alert => alert.Severity is not AlertSeverity.Ok))
        {
            Record(alert.CiId, alert.Id, alert.Severity, alert.RaisedAt);
        }

        if (failingSince.Count < 2)
        {
            return CorrelationOutcome.None;
        }

        var links = await dependencyDirectory.GetDependenciesAmongAsync(
            [.. failingSince.Keys], options.Value.CorrelationMaxDepth, cancellationToken);
        var correlations = AlertCorrelator.Correlate(
            [.. failingSince.Select(entry => new FailingCi(entry.Key, entry.Value))],
            links,
            TimeSpan.FromSeconds(options.Value.CorrelationWindowSeconds));
        if (correlations.Count == 0)
        {
            return CorrelationOutcome.None;
        }

        // Which of a cause's alerts the consequences are filed under. A switch that has failed its
        // availability rule and three interface rules holds four; the worst and oldest of them is the
        // one an operator opens, so it is the one this points at. Ties break on the id, so two
        // consecutive cycles cannot move a suppressed alert between two tickets.
        var representative = alertsByCi.ToDictionary(
            entry => entry.Key,
            entry => entry.Value
                .OrderByDescending(alert => alert.Severity)
                .ThenBy(alert => alert.RaisedAt)
                .ThenBy(alert => alert.AlertId)
                .First()
                .AlertId);

        logger.LogInformation(
            "Correlated {ImpactedCount} of {FailingCount} failing CIs to {CauseCount} root cause(s).",
            correlations.Count,
            failingSince.Count,
            correlations.Select(correlation => correlation.RootCauseCiId).Distinct().Count());

        return new CorrelationOutcome(
            correlations.ToDictionary(correlation => correlation.CiId),
            representative);
    }

    /// <summary>
    /// Pass three: re-advance every rule whose CI turned out to be a consequence, from the state it
    /// started this batch in, with the suppression the correlator found. Re-running is cheap and
    /// exact — the state machine is pure and the alert id it would allocate was fixed in pass one.
    /// <para>
    /// A rule is re-run as a whole run rather than per reading, so a batch carrying three cycles of one
    /// check still advances its counters in order.
    /// </para>
    /// </summary>
    private static void Recorrelate(List<Candidate> candidates, CorrelationOutcome correlation)
    {
        if (correlation.IsEmpty)
        {
            return;
        }

        var byRule = new Dictionary<(Guid DeviceId, string RuleId), List<int>>();
        for (var index = 0; index < candidates.Count; index++)
        {
            var key = (candidates[index].Observation.DeviceId, candidates[index].Observation.RuleId);
            if (!byRule.TryGetValue(key, out var indexes))
            {
                byRule[key] = indexes = [];
            }

            indexes.Add(index);
        }

        foreach (var indexes in byRule.Values)
        {
            var first = candidates[indexes[0]];
            if (!correlation.Impacted.ContainsKey(first.Observation.CiId))
            {
                continue;
            }

            // Suppression prevents a ticket; it can never un-open one. A rule somebody has already been
            // told about keeps its published state and only gains the grouping — re-suppressing it here
            // would leave the alert row claiming nobody was told, which is what WP-3.5's Redis rebuild
            // reads to decide whether to publish. It would re-raise the same alert after a flush.
            if (first.PriorState.PublishedSeverity is not AlertSeverity.Ok)
            {
                continue;
            }

            var state = first.PriorState;
            foreach (var index in indexes)
            {
                var candidate = candidates[index];
                var transition = AlertStateMachine.Advance(
                    state,
                    candidate.Observation.Severity,
                    candidate.Observation.ObservedAt,
                    candidate.Observation.Value,
                    candidate.Policy,
                    candidate.Muted,
                    candidate.NewAlertId,
                    explainedByRootCause: true);
                candidates[index] = candidate with { Transition = transition };
                state = transition.State;
            }
        }
    }

    /// <summary>
    /// Files the open alerts this batch did not touch under whatever now explains them — and only that.
    /// <para>
    /// Their <see cref="Alert.Suppression"/> is deliberately left alone: the state machine did not run
    /// for these rules, so writing a suppression onto them would be recording a decision nothing made,
    /// and WP-3.5 reads that field to rebuild whether the alert was ever published. The grouping is
    /// safe because it says only "this is related to that", which is true whether or not anybody was
    /// told about it.
    /// </para>
    /// </summary>
    private static void RegroupUntouchedAlerts(IReadOnlyList<Alert> untouched, CorrelationOutcome correlation)
    {
        foreach (var alert in untouched)
        {
            alert.RootCauseAlertId = correlation.RootCauseAlertFor(alert.CiId, alert.Id);
        }
    }

    /// <summary>
    /// State for every rule this result has a reading for, with anything Redis has forgotten rebuilt from the open
    /// alert row. This is what stops a Redis flush re-raising every alert in the estate: the counters
    /// and the flap history start again, but "this rule is already alerting, and it was published"
    /// comes from Postgres.
    /// </summary>
    private async Task<Dictionary<string, AlertState>> LoadStateAsync(
        Guid deviceId,
        IReadOnlyList<string?> ruleIds,
        IReadOnlyDictionary<(Guid DeviceId, string RuleId), Alert> open,
        IReadOnlyDictionary<(Guid DeviceId, string RuleId), AlertState> working,
        CancellationToken cancellationToken)
    {
        var state = new Dictionary<string, AlertState>(StringComparer.Ordinal);
        foreach (var ruleId in ruleIds)
        {
            if (ruleId is null)
            {
                continue;
            }

            // What this batch has already decided about the rule outranks both Redis and the row, and
            // has to: nothing is written until every pass has run, so Redis still holds the state this
            // batch started from.
            if (working.TryGetValue((deviceId, ruleId), out var pending))
            {
                state[ruleId] = pending;
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
        string pollerName,
        CorrelationOutcome correlation)
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
            // Rewritten on every reading rather than only when it is set, so a cause that recovers
            // un-files its consequences on their next cycle instead of leaving them pointing at an
            // explanation that no longer holds.
            alert.RootCauseAlertId = correlation.RootCauseAlertFor(observation.CiId, alert.Id);
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
        // A recovered alert explains nothing and is explained by nothing. Kept null rather than
        // preserved as history because the row is now the record of a problem that ended, and a
        // cleared alert filed under a cause reads on the board as one that is still suppressed.
        alert.RootCauseAlertId = null;
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

    /// <summary>
    /// One rule's reading, advanced but not yet committed. <see cref="PriorState"/> and
    /// <see cref="NewAlertId"/> are what make the third pass able to re-run it and get the same answer
    /// for everything except the suppression.
    /// </summary>
    private sealed record Candidate(
        AlertObservation Observation,
        AlertState PriorState,
        AlertTransition Transition,
        AlertPolicy Policy,
        Guid NewAlertId,
        bool Muted);

    /// <summary>One open alert reduced to what choosing a CI's representative alert needs.</summary>
    private sealed record OpenAlertFact(Guid AlertId, AlertSeverity Severity, DateTimeOffset RaisedAt);

    /// <summary>
    /// What the correlator decided, in the form the rest of the pass needs it: which CIs are
    /// consequences, and which alert stands for each CI that is a cause.
    /// </summary>
    private sealed record CorrelationOutcome(
        IReadOnlyDictionary<Guid, AlertCorrelation> Impacted,
        IReadOnlyDictionary<Guid, Guid> RepresentativeAlertByCi)
    {
        /// <summary>Nothing is explained by anything — every estate on almost every cycle.</summary>
        public static CorrelationOutcome None { get; } = new(
            new Dictionary<Guid, AlertCorrelation>(),
            new Dictionary<Guid, Guid>());

        public bool IsEmpty => Impacted.Count == 0;

        /// <summary>
        /// The alert <paramref name="ciId"/> should be filed under, or null when it is a cause in its
        /// own right. Never answers with <paramref name="alertId"/> itself: an alert filed under itself
        /// would render on the board as a group containing only its own header.
        /// </summary>
        public Guid? RootCauseAlertFor(Guid ciId, Guid alertId)
        {
            if (!Impacted.TryGetValue(ciId, out var correlation)
                || !RepresentativeAlertByCi.TryGetValue(correlation.RootCauseCiId, out var rootCauseAlertId)
                || rootCauseAlertId == alertId)
            {
                return null;
            }

            return rootCauseAlertId;
        }
    }
}
