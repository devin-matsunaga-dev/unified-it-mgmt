using System.Security.Claims;

using Contracts.Events;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Modules.Helpdesk.Data;
using Modules.Helpdesk.Features.Interactions;
using Modules.Helpdesk.Features.TicketCis;
using Modules.Helpdesk.Features.Tickets;

using Platform.Auditing;
using Platform.Integration;
using Platform.Notifications;

namespace Modules.Helpdesk.Features.AlertTickets;

public interface IAlertTicketAutomation
{
    Task RaiseAsync(AlertRaised alert, CancellationToken cancellationToken);

    Task ClearAsync(AlertCleared alert, CancellationToken cancellationToken);

    /// <summary>
    /// Puts the result of an auto-remediation run onto the ticket for its alert, and opens one when it
    /// failed and there is none (WP-5.6).
    /// </summary>
    Task RecordRunbookResultAsync(RunbookExecutionCompleted execution, CancellationToken cancellationToken);
}

/// <summary>
/// Everything impure about alert→ticket automation: the durable dedupe row, the ticket writes, the
/// bounds and the admin notice. The decisions themselves live in <see cref="AlertTicketPolicy"/>,
/// <see cref="TicketStatusPath"/> and <see cref="IAlertAutomationGuard"/>, which is why none of them
/// can see any of this — the same split WP-3.5 made between its engine and its state machine.
/// <para>
/// Every ticket write goes through <see cref="ITicketService"/> and
/// <see cref="IInteractionService"/> rather than the DbContext, so an automated ticket is validated,
/// SLA-clocked, audited and published exactly like one an agent typed. The cost is a transaction per
/// write, which is right for something that happens once per problem.
/// </para>
/// </summary>
public sealed class AlertTicketAutomation(
    HelpdeskDbContext dbContext,
    ITicketService ticketService,
    IInteractionService interactionService,
    ITicketCiLinkService ciLinkService,
    ICiDirectory ciDirectory,
    ITicketLinkDirectory ticketLinkDirectory,
    IAlertCorrelationDirectory alertCorrelationDirectory,
    IAlertAutomationGuard guard,
    IAuditService auditService,
    INotificationService notificationService,
    IOptions<AlertTicketOptions> options,
    ILogger<AlertTicketAutomation> logger) : IAlertTicketAutomation
{
    /// <summary>
    /// Not an agent and not an end user. It matters that it is neither: the ticket surfaces enforce
    /// "agent-only" by refusing <c>EndUser</c> rather than by requiring a role, so this actor can
    /// write, and every row it writes says plainly that nobody performed it.
    /// </summary>
    private static readonly ClaimsPrincipal SystemActor = new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, "system:monitoring"),
            new Claim(ClaimTypes.Name, "Monitoring"),
        ],
        "Monitoring"));

    /// <summary>
    /// How many already-open tickets about the same CI the description names. A ticket that listed
    /// twenty would be reporting the estate rather than the alert.
    /// </summary>
    private const int OpenRelatedTicketLimit = 5;

    public async Task RaiseAsync(AlertRaised alert, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(alert);
        if (!options.Value.Enabled)
        {
            logger.LogDebug("Alert→ticket automation is disabled; {RuleId} opened no ticket.", alert.RuleId);
            return;
        }

        var key = AlertTicketPolicy.DedupeKey(alert.DeviceId, alert.RuleId);
        var entry = await ClaimAsync(key, alert, cancellationToken);
        var before = Snapshot(entry);

        var previousSeverity = entry.LastSeverity;
        var isFirstRaise = entry.OccurrenceCount == 0;
        entry.OccurrenceCount++;
        entry.AlertId = alert.AlertId;
        entry.LastSeverity = alert.Severity;
        entry.LastRaisedAt = alert.RaisedAt;

        var existing = await LoadTicketAsync(entry.TicketId, cancellationToken);
        if (existing is not null && !IsFinished(existing))
        {
            // The heart of the WP: the same problem, again, is a note on the ticket that already
            // exists. Internal, because the requester of an automated ticket is the platform itself
            // and a public comment would mail nobody while looking like it had mailed somebody.
            await CommentAsync(
                existing.Id,
                AlertTicketPolicy.RecurrenceNote(alert, entry.OccurrenceCount, isFirstRaise ? null : previousSeverity),
                cancellationToken);
            await SaveAsync(entry, before, "Annotated", cancellationToken);
            logger.LogInformation(
                "Alert {RuleId} recurred at {Severity}; annotated {TicketNumber} rather than opening a second ticket.",
                alert.RuleId, alert.Severity, existing.Number);
            return;
        }

        var decision = await guard.EvaluateAsync(
            key, CountRecentTicketsAsync, alert.RaisedAt, cancellationToken);
        if (!decision.IsAllowed)
        {
            entry.SuppressedCount++;
            await SaveAsync(entry, before, "Suppressed", cancellationToken);
            logger.LogWarning(
                "Alert {RuleId} opened no ticket: {Reason}. Suppressed {SuppressedCount} time(s) for this rule.",
                alert.RuleId, decision.Reason, entry.SuppressedCount);
            if (decision.Verdict == AutomationVerdict.BreakerTripped)
            {
                await NotifyBreakerAsync(decision, alert, cancellationToken);
            }

            return;
        }

        // Read before the ticket exists, so "open related tickets" cannot list the one being opened.
        var context = await DescribeCiAsync(alert.CiId, cancellationToken);
        var impacted = await DescribeImpactAsync(alert, cancellationToken);
        var draft = AlertTicketPolicy.Compose(alert, context, impacted);
        var created = await ticketService.CreateAsync(
            new CreateTicketRequest(
                draft.Title,
                draft.Description,
                TicketType.Incident,
                draft.Urgency,
                draft.Impact,
                RequesterId: null,
                QueueId: await ResolveQueueIdAsync(cancellationToken)),
            SystemActor,
            cancellationToken);
        if (created.Outcome != TicketWriteOutcome.Success || created.Ticket is null)
        {
            // Nothing to retry against: the draft is composed from the event and does not depend on
            // anything that could have been fixed since. The row keeps the occurrence so the raise is
            // not lost, and the alert stays visible on the monitoring side.
            logger.LogError(
                "Alert {RuleId} could not be ticketed: {Outcome}.", alert.RuleId, created.Outcome);
            await SaveAsync(entry, before, "TicketFailed", cancellationToken);
            return;
        }

        await LinkCiAsync(created.Ticket.Id, alert, context, cancellationToken);

        if (existing is not null)
        {
            // The WP-1.2 graph has no edge out of Resolved or Closed, so a rule that recurs after its
            // ticket was finished gets a new one. The two are joined by a note in each direction —
            // silently starting again is what makes a ticket history unreadable.
            await CommentAsync(
                existing.Id,
                AlertTicketPolicy.SupersededNote(created.Ticket.Number, alert.Severity),
                cancellationToken);
            await CommentAsync(
                created.Ticket.Id,
                AlertTicketPolicy.SupersedesNote(existing.Number),
                cancellationToken);
        }

        entry.TicketId = created.Ticket.Id;
        entry.TicketCreatedAt = created.Ticket.CreatedAt;
        entry.TicketCount++;
        entry.AutoResolvedAt = null;
        await SaveAsync(entry, before, "TicketOpened", cancellationToken);
        logger.LogInformation(
            "Alert {RuleId} at {Severity} opened {TicketNumber}.",
            alert.RuleId, alert.Severity, created.Ticket.Number);
    }

    public async Task ClearAsync(AlertCleared alert, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(alert);
        var key = AlertTicketPolicy.DedupeKey(alert.DeviceId, alert.RuleId);
        var entry = await dbContext.AlertTickets
            .SingleOrDefaultAsync(item => item.DedupeKey == key, cancellationToken);
        if (entry is null)
        {
            // A clear for something this platform never ticketed: the automation was off, or the raise
            // was suppressed before any row existed. A fact rather than a fault.
            logger.LogInformation("Alert {RuleId} cleared but had no ticket record.", alert.RuleId);
            return;
        }

        var before = Snapshot(entry);
        entry.LastClearedAt = alert.OccurredAt;

        var ticket = await LoadTicketAsync(entry.TicketId, cancellationToken);
        if (ticket is null)
        {
            await SaveAsync(entry, before, "Cleared", cancellationToken);
            return;
        }

        var note = AlertTicketPolicy.ResolutionNote(alert);
        await CommentAsync(ticket.Id, note, cancellationToken);
        if (IsFinished(ticket))
        {
            // Somebody resolved it by hand first. The note above still lands, because "monitoring
            // agrees this is over" is worth having on the ticket.
            await SaveAsync(entry, before, "Cleared", cancellationToken);
            return;
        }

        if (await AdvanceToResolvedAsync(ticket, note, cancellationToken))
        {
            entry.AutoResolvedAt = alert.OccurredAt;
            await SaveAsync(entry, before, "AutoResolved", cancellationToken);
            logger.LogInformation(
                "Alert {RuleId} cleared after {DurationSeconds}s; auto-resolved {TicketNumber}.",
                alert.RuleId, alert.DurationSeconds, ticket.Number);
            return;
        }

        await SaveAsync(entry, before, "Cleared", cancellationToken);
    }

    /// <summary>
    /// The WP-5.6 half: Monitoring ran something on a machine, and this is where the result becomes
    /// something a person will see.
    /// <para>
    /// It lives here, on the Helpdesk side of an event, because Monitoring may not write a ticket — and
    /// because Helpdesk already holds the only thing that can find the right one: the
    /// <c>alert:{deviceId}:{ruleId}</c> dedupe row this class writes when the alert is first ticketed.
    /// </para>
    /// <para>
    /// A success is recorded and nothing more. A failure always ends with a human-facing ticket: the
    /// alert's own if there is one, a new one if there is not — including for a run somebody started by
    /// hand, because the result arrives long after their request returned and there is otherwise
    /// nothing to notice it.
    /// </para>
    /// </summary>
    public async Task RecordRunbookResultAsync(
        RunbookExecutionCompleted execution,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(execution);
        var succeeded = execution.Outcome.Equals("Succeeded", StringComparison.OrdinalIgnoreCase);

        if (!options.Value.Enabled)
        {
            // The alert→ticket switch governs this too. With it off there is no ticket to annotate, and
            // opening one for a failed runbook would be the automation writing tickets while turned off.
            logger.LogInformation(
                "Runbook {RunbookKey} finished as {Outcome}; alert→ticket automation is disabled, so nothing was written to a ticket.",
                execution.RunbookKey, execution.Outcome);
            return;
        }

        var ticket = await FindAlertTicketAsync(execution, cancellationToken);
        if (ticket is not null)
        {
            // Left even on a finished ticket, following the clear path above: "the automation tried this
            // and here is what happened" belongs on the ticket whether or not somebody has closed it.
            await CommentAsync(ticket.Id, AlertTicketPolicy.RunbookNote(execution), cancellationToken);
            logger.LogInformation(
                "Runbook {RunbookKey} finished as {Outcome}; recorded on {TicketNumber}.",
                execution.RunbookKey, execution.Outcome, ticket.Number);
            return;
        }

        if (succeeded)
        {
            logger.LogInformation(
                "Runbook {RunbookKey} succeeded on device {DeviceId} with no ticket open for it; nothing was opened.",
                execution.RunbookKey, execution.DeviceId);
            return;
        }

        await EscalateRunbookAsync(execution, cancellationToken);
    }

    /// <summary>
    /// The ticket this execution's alert opened, or null — for a manual run, for an alert whose ticket
    /// was suppressed, or for one the automation never saw.
    /// </summary>
    private async Task<Ticket?> FindAlertTicketAsync(
        RunbookExecutionCompleted execution,
        CancellationToken cancellationToken)
    {
        if (execution.AlertId is null || string.IsNullOrWhiteSpace(execution.RuleId))
        {
            return null;
        }

        var key = AlertTicketPolicy.DedupeKey(execution.DeviceId, execution.RuleId);
        var entry = await dbContext.AlertTickets.AsNoTracking()
            .SingleOrDefaultAsync(item => item.DedupeKey == key, cancellationToken);
        return entry is null ? null : await LoadTicketAsync(entry.TicketId, cancellationToken);
    }

    /// <summary>
    /// A failed remediation with nowhere to report itself gets a ticket of its own.
    /// <para>
    /// It deliberately does not claim the alert's dedupe row. That row is the alert's, and taking it
    /// would mean the alert itself — still unresolved, still failing — could never open the ticket it
    /// is entitled to.
    /// </para>
    /// </summary>
    private async Task EscalateRunbookAsync(
        RunbookExecutionCompleted execution,
        CancellationToken cancellationToken)
    {
        var draft = AlertTicketPolicy.ComposeRunbookEscalation(execution);
        var created = await ticketService.CreateAsync(
            new CreateTicketRequest(
                draft.Title,
                draft.Description,
                TicketType.Incident,
                draft.Urgency,
                draft.Impact,
                RequesterId: null,
                QueueId: await ResolveQueueIdAsync(cancellationToken)),
            SystemActor,
            cancellationToken);
        if (created.Outcome != TicketWriteOutcome.Success || created.Ticket is null)
        {
            // Loud, because this is the escalation path failing. The execution row and the audit entry
            // still hold the result, and the monitoring side has already routed a notification about it.
            logger.LogError(
                "Runbook {RunbookKey} failed and its escalation ticket could not be opened: {Outcome}.",
                execution.RunbookKey, created.Outcome);
            return;
        }

        await LinkCiIfKnownAsync(created.Ticket.Id, execution.CiId, execution.RunbookKey, cancellationToken);
        logger.LogWarning(
            "Runbook {RunbookKey} {Outcome} on device {DeviceId} with no ticket to record it on; opened {TicketNumber}.",
            execution.RunbookKey, execution.Outcome, execution.DeviceId, created.Ticket.Number);
    }

    /// <summary>
    /// Links the CI if the CMDB still has it, and never fails the ticket for it — the same rule
    /// <see cref="LinkCiAsync"/> follows, for the same reason.
    /// </summary>
    private async Task LinkCiIfKnownAsync(
        Guid ticketId,
        Guid ciId,
        string context,
        CancellationToken cancellationToken)
    {
        if (ciId == Guid.Empty)
        {
            return;
        }

        var ci = (await ciDirectory.GetSummariesAsync([ciId], cancellationToken)).SingleOrDefault();
        if (ci is null)
        {
            logger.LogInformation(
                "Runbook {RunbookKey} names CI {CiId}, which is not in the CMDB; its ticket was not linked.",
                context, ciId);
            return;
        }

        var result = await ciLinkService.LinkAsync(
            ticketId, new LinkTicketCiRequest(ciId), SystemActor, cancellationToken);
        if (result.Outcome is not TicketCiLinkOutcome.Success)
        {
            logger.LogWarning(
                "Runbook {RunbookKey} could not link CI {CiId} to its ticket: {Outcome}.",
                context, ciId, result.Outcome);
        }
    }

    /// <summary>
    /// What the CMDB knows about the CI this alert names (WP-3.7). Both reads go through ports, so
    /// Helpdesk still never queries the assets schema, and a CI that is not there is a context that
    /// says so rather than a failure — a device can be monitored and its CI deleted, and the alert is
    /// still worth a ticket.
    /// </summary>
    private async Task<AlertCiContext> DescribeCiAsync(Guid ciId, CancellationToken cancellationToken)
    {
        if (ciId == Guid.Empty)
        {
            return AlertCiContext.Unknown;
        }

        var ci = (await ciDirectory.GetSummariesAsync([ciId], cancellationToken)).SingleOrDefault();
        var open = await ticketLinkDirectory.GetOpenTicketsForCiAsync(
            ciId, OpenRelatedTicketLimit, cancellationToken);
        return new AlertCiContext(ci, open);
    }

    /// <summary>
    /// The CIs failing underneath this alert (WP-5.1), named rather than listed as ids. Two port reads,
    /// both on the path that opens a ticket — which happens once per problem — and neither on the path
    /// that annotates one, because a recurrence is not a new outage to enumerate.
    /// <para>
    /// A failure here never fails the raise, following the same rule as the CI link below: the ticket
    /// is the thing somebody has to act on, and a root-cause ticket that lists nothing is a degraded
    /// ticket rather than a lost alert. The alerts themselves are already suppressed and visible on the
    /// board either way.
    /// </para>
    /// </summary>
    private async Task<IReadOnlyList<ImpactedCi>> DescribeImpactAsync(
        AlertRaised alert,
        CancellationToken cancellationToken)
    {
        try
        {
            var suppressed = await alertCorrelationDirectory.GetImpactedByAsync(alert.AlertId, cancellationToken);
            if (suppressed.Count == 0)
            {
                return [];
            }

            var ids = suppressed.Select(entry => entry.CiId).Where(id => id != Guid.Empty).Distinct().ToList();
            var names = (await ciDirectory.GetSummariesAsync(ids, cancellationToken))
                .ToDictionary(ci => ci.Id);

            return
            [
                .. suppressed.Select(entry => new ImpactedCi(
                    entry.CiId,
                    names.TryGetValue(entry.CiId, out var ci) ? ci.Name : null,
                    names.TryGetValue(entry.CiId, out var typed) ? typed.Type : null,
                    entry.Summary)),
            ];
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception,
                "Alert {RuleId} could not read what it is suppressing; its ticket lists no affected CIs.",
                alert.RuleId);
            return [];
        }
    }

    /// <summary>
    /// The other half of "carries CMDB context": the ticket is linked to its CI through the same
    /// service an agent's "Link asset" button calls, so the link is audited, published and visible
    /// from both sides — the ticket's asset card and the CI's ticket list.
    /// <para>
    /// A failure here never fails the raise. The ticket is the thing somebody has to act on; a missing
    /// link is a degraded ticket rather than a lost alert, and the description still names the CI.
    /// </para>
    /// </summary>
    private async Task LinkCiAsync(
        Guid ticketId,
        AlertRaised alert,
        AlertCiContext context,
        CancellationToken cancellationToken)
    {
        if (alert.CiId == Guid.Empty || context.Ci is null)
        {
            logger.LogInformation(
                "Alert {RuleId} names CI {CiId}, which is not in the CMDB; its ticket was not linked.",
                alert.RuleId, alert.CiId);
            return;
        }

        var result = await ciLinkService.LinkAsync(
            ticketId, new LinkTicketCiRequest(alert.CiId), SystemActor, cancellationToken);
        if (result.Outcome is not TicketCiLinkOutcome.Success)
        {
            logger.LogWarning(
                "Alert {RuleId} could not link CI {CiId} to its ticket: {Outcome}.",
                alert.RuleId, alert.CiId, result.Outcome);
        }
    }

    /// <summary>
    /// Takes the ticket to Resolved along whatever route the transition graph permits, one guarded
    /// hop at a time. A hop that is refused stops the walk and leaves the ticket where it stands: an
    /// agent who moved it mid-clear has a better claim on its status than this does.
    /// </summary>
    private async Task<bool> AdvanceToResolvedAsync(
        Ticket ticket,
        string resolutionNote,
        CancellationToken cancellationToken)
    {
        var statuses = await dbContext.TicketStatuses.ToDictionaryAsync(status => status.Id, cancellationToken);
        var edges = await dbContext.TicketStatusTransitions
            .Select(transition => new { transition.FromStatusId, transition.ToStatusId })
            .ToListAsync(cancellationToken);
        var path = TicketStatusPath.Find(
            [.. edges.Select(edge => (edge.FromStatusId, edge.ToStatusId))],
            ticket.StatusId,
            DefaultTicketStatuses.ResolvedId);
        if (path is null)
        {
            logger.LogWarning(
                "Ticket {TicketNumber} cannot reach Resolved from {Status}; it was left as it is.",
                ticket.Number, statuses[ticket.StatusId].Name);
            return false;
        }

        foreach (var statusId in path)
        {
            var status = statuses[statusId];
            var result = await ticketService.TransitionAsync(
                ticket.Id,
                new TransitionTicketRequest(status.Name, status.RequiresResolutionNote ? resolutionNote : null),
                SystemActor,
                cancellationToken);
            if (result.Outcome != TransitionTicketOutcome.Success)
            {
                logger.LogWarning(
                    "Ticket {TicketNumber} could not be moved to {Status} while auto-resolving: {Error}",
                    ticket.Number, status.Name, result.Error);
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Claims the dedupe key before anything else happens. Two consumers racing on one rule both try
    /// to insert; the loser's unique-index violation faults its message, and the retry finds the
    /// winner's row and annotates it. That is what makes "one ticket per alert" a database constraint
    /// rather than only a hope about ordering.
    /// </summary>
    private async Task<AlertTicket> ClaimAsync(string key, AlertRaised alert, CancellationToken cancellationToken)
    {
        var existing = await dbContext.AlertTickets
            .SingleOrDefaultAsync(item => item.DedupeKey == key, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        var entry = new AlertTicket
        {
            Id = Guid.CreateVersion7(),
            DedupeKey = key,
            DeviceId = alert.DeviceId,
            CiId = alert.CiId,
            RuleId = alert.RuleId,
            AlertId = alert.AlertId,
            LastSeverity = alert.Severity,
            FirstRaisedAt = alert.RaisedAt,
            LastRaisedAt = alert.RaisedAt,
        };
        dbContext.AlertTickets.Add(entry);
        await dbContext.SaveChangesAsync(cancellationToken);
        return entry;
    }

    private Task<int> CountRecentTicketsAsync(DateTimeOffset since, CancellationToken cancellationToken) =>
        dbContext.AlertTickets.CountAsync(item => item.TicketCreatedAt >= since, cancellationToken);

    private Task<Ticket?> LoadTicketAsync(Guid? ticketId, CancellationToken cancellationToken) =>
        ticketId is null
            ? Task.FromResult<Ticket?>(null)
            : dbContext.Tickets.Include(ticket => ticket.Status)
                .SingleOrDefaultAsync(ticket => ticket.Id == ticketId, cancellationToken);

    private static bool IsFinished(Ticket ticket) =>
        ticket.StatusId == DefaultTicketStatuses.ResolvedId || ticket.StatusId == DefaultTicketStatuses.ClosedId;

    private Task CommentAsync(Guid ticketId, string body, CancellationToken cancellationToken) =>
        interactionService.AddCommentAsync(
            ticketId, new CreateCommentRequest(body, IsInternal: true), SystemActor, cancellationToken);

    private async Task SaveAsync(
        AlertTicket entry,
        object before,
        string action,
        CancellationToken cancellationToken)
    {
        await dbContext.SaveChangesAsync(cancellationToken);
        // Audited under the system actor, following WP-3.2's missed heartbeat and WP-3.5's raise —
        // which is also what flushes the outbox for anything the ticket writes published.
        await auditService.WriteAsync(
            SystemActor, action, "AlertTicket", entry.Id.ToString(), before, Snapshot(entry), cancellationToken);
    }

    private async Task NotifyBreakerAsync(
        AutomationDecision decision,
        AlertRaised alert,
        CancellationToken cancellationToken)
    {
        var settings = options.Value;
        await notificationService.SendAsync(new NotificationMessage(
            settings.AdminRecipient,
            new NotificationTemplate(
                "AlertTicketBreakerTripped",
                "Alert→ticket automation has stopped opening tickets",
                string.Join(Environment.NewLine,
                    $"The alert→ticket circuit breaker tripped: {decision.Reason}.",
                    $"It stays open for {settings.BreakerCooldownSeconds}s, during which alerts are recorded but no tickets are opened.",
                    $"The alert that tripped it: {alert.RuleId} on device {alert.DeviceId} at {alert.Severity}.",
                    "Alerts themselves are unaffected — check the monitoring side for what is actually failing.")),
            new { alert.RuleId, alert.DeviceId, alert.Severity }), cancellationToken);
        logger.LogError(
            "Alert→ticket circuit breaker tripped ({Reason}); {Recipient} was notified.",
            decision.Reason, settings.AdminRecipient);
    }

    private static object Snapshot(AlertTicket entry) => new
    {
        entry.DedupeKey,
        entry.RuleId,
        entry.TicketId,
        entry.LastSeverity,
        entry.OccurrenceCount,
        entry.SuppressedCount,
        entry.TicketCount,
        entry.LastRaisedAt,
        entry.LastClearedAt,
        entry.AutoResolvedAt,
    };

    private async Task<Guid?> ResolveQueueIdAsync(CancellationToken cancellationToken)
    {
        // By name, then the first queue, then none — the WP-1.8 portal rule. An automated ticket that
        // enters no queue is never round-robined to anybody, so falling back is better than insisting.
        var name = options.Value.QueueName;
        var queue = await dbContext.TicketQueues
            .Where(item => item.Name == name)
            .Select(item => (Guid?)item.Id)
            .SingleOrDefaultAsync(cancellationToken);
        return queue ?? await dbContext.TicketQueues
            .OrderBy(item => item.Name)
            .Select(item => (Guid?)item.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }
}
