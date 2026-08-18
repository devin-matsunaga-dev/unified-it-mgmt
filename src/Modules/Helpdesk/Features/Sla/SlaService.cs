using System.Security.Claims;

using Contracts.Events;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Modules.Helpdesk.Data;
using Platform.Auditing;
using Platform.Data;
using Platform.Notifications;

namespace Modules.Helpdesk.Features.Sla;

public sealed class SlaService(
    HelpdeskDbContext dbContext,
    IPublishEndpoint publishEndpoint,
    INotificationRouter notificationRouter,
    IOptions<NotificationOptions> notificationOptions,
    IAuditService auditService) : ISlaService
{
    private static readonly ClaimsPrincipal SchedulerActor = new(new ClaimsIdentity(
        [new Claim(ClaimTypes.NameIdentifier, "system:sla-scheduler")], "Scheduler"));

    public async Task<BusinessHoursCalendarResponse> CreateCalendarAsync(
        CreateBusinessHoursCalendarRequest request, ClaimsPrincipal actor, CancellationToken cancellationToken)
    {
        _ = TimeZoneInfo.FindSystemTimeZoneById(request.TimeZoneId);
        var calendar = new BusinessHoursCalendar
        {
            Id = Guid.CreateVersion7(), Name = request.Name.Trim(), TimeZoneId = request.TimeZoneId,
            WorkingDays = request.WorkingDays, StartTime = request.StartTime, EndTime = request.EndTime,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        dbContext.BusinessHoursCalendars.Add(calendar);
        await dbContext.SaveChangesAsync(cancellationToken);
        var response = Map(calendar);
        await auditService.WriteAsync(actor, "Created", "BusinessHoursCalendar", calendar.Id.ToString(), null, response, cancellationToken);
        return response;
    }

    public async Task<SlaPolicyResult> CreatePolicyAsync(
        SavePolicyRequest request, ClaimsPrincipal actor, CancellationToken cancellationToken)
    {
        if (await ReferenceProblemAsync(request, cancellationToken) is { } problem) return problem;

        var policy = new SlaPolicy
        {
            Id = Guid.CreateVersion7(),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        Apply(policy, request);
        dbContext.SlaPolicies.Add(policy);
        await dbContext.SaveChangesAsync(cancellationToken);

        var response = await ReadPolicyAsync(policy.Id, cancellationToken);
        await auditService.WriteAsync(actor, "Created", "SlaPolicy", policy.Id.ToString(), null, response, cancellationToken);
        return new(SlaOutcome.Success, response);
    }

    public async Task<SlaPolicyResult> UpdatePolicyAsync(
        Guid id, SavePolicyRequest request, ClaimsPrincipal actor, CancellationToken cancellationToken)
    {
        var policy = await dbContext.SlaPolicies.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (policy is null) return new(SlaOutcome.NotFound);
        if (await ReferenceProblemAsync(request, cancellationToken) is { } problem) return problem;

        var before = await ReadPolicyAsync(id, cancellationToken);
        Apply(policy, request);
        await dbContext.SaveChangesAsync(cancellationToken);

        // Tickets already running keep the targets they started with, which is the whole point of
        // snapshotting them onto TicketSla. This edit reaches new tickets only.
        var after = await ReadPolicyAsync(id, cancellationToken);
        await auditService.WriteAsync(actor, "Updated", "SlaPolicy", id.ToString(), before, after, cancellationToken);
        return new(SlaOutcome.Success, after);
    }

    public async Task<SlaOutcome> DeletePolicyAsync(Guid id, ClaimsPrincipal actor, CancellationToken cancellationToken)
    {
        var policy = await dbContext.SlaPolicies.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (policy is null) return SlaOutcome.NotFound;

        // A policy any ticket has run against stays, so the clock on that ticket remains explainable.
        // Deactivating it is the way to retire one.
        if (await dbContext.TicketSlas.AnyAsync(item => item.PolicyId == id, cancellationToken))
        {
            return SlaOutcome.InUse;
        }

        var before = await ReadPolicyAsync(id, cancellationToken);
        dbContext.SlaPolicies.Remove(policy);
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditService.WriteAsync(actor, "Deleted", "SlaPolicy", id.ToString(), before, null, cancellationToken);
        return SlaOutcome.Success;
    }

    public async Task<IReadOnlyList<SlaPolicyResponse>> ListPoliciesAsync(CancellationToken cancellationToken)
    {
        var counts = await TicketCountsAsync(cancellationToken);
        var policies = await dbContext.SlaPolicies
            .Include(item => item.Calendar).Include(item => item.Category)
            .OrderBy(item => item.SortOrder).ThenBy(item => item.CreatedAt)
            .ToListAsync(cancellationToken);
        return [.. policies.Select(item => Map(item, counts.GetValueOrDefault(item.Id)))];
    }

    /// <summary>
    /// Renumbers the named policies from zero, in the order given. Anything not named keeps its
    /// place after them, so a partial list cannot silently reshuffle the rest.
    /// </summary>
    public async Task ReorderPoliciesAsync(
        IReadOnlyList<Guid> policyIds, ClaimsPrincipal actor, CancellationToken cancellationToken)
    {
        var policies = await dbContext.SlaPolicies.ToDictionaryAsync(item => item.Id, cancellationToken);
        var order = 0;
        foreach (var id in policyIds)
        {
            if (policies.TryGetValue(id, out var policy)) policy.SortOrder = order++;
        }

        foreach (var policy in policies.Values.Where(item => !policyIds.Contains(item.Id))
            .OrderBy(item => item.SortOrder).ThenBy(item => item.CreatedAt))
        {
            policy.SortOrder = order++;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await auditService.WriteAsync(
            actor, "Reordered", "SlaPolicy", "all", null, new { PolicyIds = policyIds }, cancellationToken);
    }

    public async Task<IReadOnlyList<BusinessHoursCalendarResponse>> ListCalendarsAsync(CancellationToken cancellationToken)
    {
        var usage = await dbContext.SlaPolicies
            .GroupBy(policy => policy.CalendarId)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);
        var counts = usage.ToDictionary(row => row.Key, row => row.Count);
        var calendars = await dbContext.BusinessHoursCalendars.OrderBy(item => item.Name)
            .ToListAsync(cancellationToken);
        return [.. calendars.Select(item => Map(item) with { PolicyCount = counts.GetValueOrDefault(item.Id) })];
    }

    public async Task<SlaOutcome> DeleteCalendarAsync(Guid id, ClaimsPrincipal actor, CancellationToken cancellationToken)
    {
        var calendar = await dbContext.BusinessHoursCalendars.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (calendar is null) return SlaOutcome.NotFound;
        if (await dbContext.SlaPolicies.AnyAsync(item => item.CalendarId == id, cancellationToken)
            || await dbContext.TicketSlas.AnyAsync(item => item.CalendarId == id, cancellationToken))
        {
            return SlaOutcome.InUse;
        }

        var before = Map(calendar);
        dbContext.BusinessHoursCalendars.Remove(calendar);
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditService.WriteAsync(actor, "Deleted", "BusinessHoursCalendar", id.ToString(), before, null, cancellationToken);
        return SlaOutcome.Success;
    }

    private static void Apply(SlaPolicy policy, SavePolicyRequest request)
    {
        policy.Name = request.Name.Trim();
        policy.SortOrder = request.SortOrder;
        policy.Priority = request.Priority;
        policy.TicketType = request.TicketType;
        policy.CategoryId = request.CategoryId;
        policy.ResponseTargetMinutes = request.ResponseTargetMinutes;
        policy.ResolutionTargetMinutes = request.ResolutionTargetMinutes;
        policy.WarningPercent = request.WarningPercent;
        policy.CalendarId = request.CalendarId;
        policy.IsActive = request.IsActive;
    }

    private async Task<SlaPolicyResult?> ReferenceProblemAsync(
        SavePolicyRequest request, CancellationToken cancellationToken)
    {
        if (!await dbContext.BusinessHoursCalendars.AnyAsync(item => item.Id == request.CalendarId, cancellationToken))
        {
            return new(SlaOutcome.CalendarNotFound);
        }

        if (request.CategoryId is { } categoryId
            && !await dbContext.TicketCategories.AnyAsync(item => item.Id == categoryId, cancellationToken))
        {
            return new(SlaOutcome.CategoryNotFound);
        }

        return null;
    }

    private async Task<Dictionary<Guid, int>> TicketCountsAsync(CancellationToken cancellationToken)
    {
        var rows = await dbContext.TicketSlas
            .GroupBy(item => item.PolicyId)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);
        return rows.ToDictionary(row => row.Key, row => row.Count);
    }

    private async Task<SlaPolicyResponse> ReadPolicyAsync(Guid id, CancellationToken cancellationToken)
    {
        var counts = await TicketCountsAsync(cancellationToken);
        var policy = await dbContext.SlaPolicies.AsNoTracking()
            .Include(item => item.Calendar).Include(item => item.Category)
            .SingleAsync(item => item.Id == id, cancellationToken);
        return Map(policy, counts.GetValueOrDefault(id));
    }

    /// <summary>
    /// Attaches the first active policy whose conditions the ticket meets, in the order an
    /// administrator arranged, and <b>copies its targets onto the ticket</b>.
    ///
    /// <para>
    /// A null condition matches anything, so a policy with none is the catch-all — which is why the
    /// order matters and why it is a column rather than an accident of creation time. The whole
    /// active list is read and matched in memory: it is a handful of rows, and expressing "null means
    /// any" three times over in SQL earns nothing.
    /// </para>
    /// </summary>
    public async Task StartAsync(Ticket ticket, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var policies = await dbContext.SlaPolicies.Include(item => item.Calendar)
            .Where(item => item.IsActive)
            .OrderBy(item => item.SortOrder).ThenBy(item => item.CreatedAt)
            .ToListAsync(cancellationToken);
        var policy = policies.FirstOrDefault(item => Matches(item, ticket));
        if (policy is null) return;

        dbContext.TicketSlas.Add(new TicketSla
        {
            Id = Guid.CreateVersion7(),
            TicketId = ticket.Id,
            PolicyId = policy.Id,
            // Snapshotted, not referenced: see TicketSla's own note.
            ResponseTargetMinutes = policy.ResponseTargetMinutes,
            ResolutionTargetMinutes = policy.ResolutionTargetMinutes,
            WarningPercent = policy.WarningPercent,
            CalendarId = policy.CalendarId,
            StartedAt = now,
            ActiveSince = now,
        });
    }

    /// <summary>Every condition the policy states must hold; the ones it leaves null are not asked.</summary>
    public static bool Matches(SlaPolicy policy, Ticket ticket) =>
        (policy.Priority is null || policy.Priority == ticket.Priority)
        && (policy.TicketType is null || policy.TicketType == ticket.Type)
        && (policy.CategoryId is null || policy.CategoryId == ticket.CategoryId);

    public async Task RecordStatusChangeAsync(
        Ticket ticket, Guid fromStatusId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var sla = await dbContext.TicketSlas.Include(item => item.Policy).Include(item => item.Calendar)
            .SingleOrDefaultAsync(item => item.TicketId == ticket.Id, cancellationToken);
        if (sla is null) return;
        if (ticket.StatusId == DefaultTicketStatuses.PendingId && sla.ActiveSince is not null)
        {
            sla.AccumulatedBusinessSeconds += BusinessTimeCalculator.Elapsed(sla.ActiveSince.Value, now, sla.Calendar).TotalSeconds;
            sla.ActiveSince = null;
        }
        else if (fromStatusId == DefaultTicketStatuses.PendingId && sla.ActiveSince is null
                 && ticket.StatusId != DefaultTicketStatuses.ResolvedId
                 && ticket.StatusId != DefaultTicketStatuses.ClosedId)
        {
            sla.ActiveSince = now;
        }

        if (ticket.StatusId is var completedStatus &&
            (completedStatus == DefaultTicketStatuses.ResolvedId || completedStatus == DefaultTicketStatuses.ClosedId))
        {
            AccumulateActive(sla, now);
            sla.ResolutionCompletedAt ??= now;
        }
    }

    public async Task MarkRespondedAsync(Guid ticketId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var sla = await dbContext.TicketSlas.Include(item => item.Policy).Include(item => item.Calendar)
            .SingleOrDefaultAsync(item => item.TicketId == ticketId, cancellationToken);
        if (sla is null || sla.ResponseCompletedAt is not null) return;
        sla.ResponseBusinessSeconds = CurrentElapsedSeconds(sla, now);
        sla.ResponseCompletedAt = now;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<SlaRemainingResponse?> GetRemainingAsync(
        Guid ticketId, ClaimsPrincipal actor, CancellationToken cancellationToken)
    {
        var sla = await dbContext.TicketSlas.Include(item => item.Ticket).Include(item => item.Policy).Include(item => item.Calendar)
            .SingleOrDefaultAsync(item => item.TicketId == ticketId, cancellationToken);
        if (sla is null || (actor.IsInRole("EndUser") && sla.Ticket.RequesterId != ActorId(actor))) return null;
        return MapRemaining(sla, DateTimeOffset.UtcNow);
    }

    public async Task EvaluateAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var slas = await dbContext.TicketSlas.Include(item => item.Ticket).ThenInclude(item => item.Queue!).ThenInclude(item => item.Team).ThenInclude(item => item.Members)
            .Include(item => item.Policy).Include(item => item.Calendar)
            .Where(item => item.ResolutionCompletedAt == null).ToListAsync(cancellationToken);
        foreach (var sla in slas)
        {
            await EvaluateTargetAsync(sla, now, true, cancellationToken);
            await EvaluateTargetAsync(sla, now, false, cancellationToken);
        }
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task EvaluateTargetAsync(TicketSla sla, DateTimeOffset now, bool response, CancellationToken cancellationToken)
    {
        if (response && sla.ResponseCompletedAt is not null) return;
        var elapsed = CurrentElapsedSeconds(sla, now);
        var target = (response ? sla.ResponseTargetMinutes : sla.ResolutionTargetMinutes) * 60d;
        var warned = response ? sla.ResponseWarningRaised : sla.ResolutionWarningRaised;
        var breached = response ? sla.ResponseBreached : sla.ResolutionBreached;
        var targetName = response ? "Response" : "Resolution";
        var dueAt = DueAt(sla, now, target);
        if (!warned && elapsed >= target * sla.WarningPercent / 100d)
        {
            if (response) sla.ResponseWarningRaised = true; else sla.ResolutionWarningRaised = true;
            await publishEndpoint.Publish(new SlaWarningRaised(Guid.CreateVersion7(), now, sla.TicketId, sla.Ticket.Number, targetName, dueAt), cancellationToken);
            await NotifySlaAsync(sla, "SlaWarningRaised", NotificationSeverity.Warning,
                $"SLA warning for {sla.Ticket.Number}",
                $"The {targetName.ToLowerInvariant()} target is approaching and is due at {dueAt:u}.",
                targetName, dueAt, cancellationToken);
            await auditService.WriteAsync(SchedulerActor, "WarningRaised", "TicketSla", sla.Id.ToString(), null, new { Target = targetName, DueAt = dueAt }, cancellationToken);
        }
        if (!breached && elapsed >= target)
        {
            if (response) sla.ResponseBreached = true; else sla.ResolutionBreached = true;
            await publishEndpoint.Publish(new SlaBreached(Guid.CreateVersion7(), now, sla.TicketId, sla.Ticket.Number, targetName, dueAt), cancellationToken);
            // A breach notified nobody before WP-3.10 — only the silent reassignment below said it had
            // happened. It is Critical, so a rule set to "Critical only" carries it.
            await NotifySlaAsync(sla, "SlaBreached", NotificationSeverity.Critical,
                $"SLA breach on {sla.Ticket.Number}",
                $"The {targetName.ToLowerInvariant()} target was due at {dueAt:u} and has been missed.",
                targetName, dueAt, cancellationToken);
            if (!response) await EscalateAssignmentAsync(sla, now, cancellationToken);
            await auditService.WriteAsync(SchedulerActor, "Breached", "TicketSla", sla.Id.ToString(), null, new { Target = targetName, DueAt = dueAt }, cancellationToken);
        }
    }

    private async Task EscalateAssignmentAsync(TicketSla sla, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var queue = sla.Ticket.Queue;
        if (queue is null) return;
        var technicians = queue.Team.Members.Select(item => item.TechnicianId).OrderBy(item => item, StringComparer.Ordinal).ToList();
        if (technicians.Count < 2) return;
        var currentIndex = technicians.FindIndex(item => item == sla.Ticket.AssignedTechnicianId);
        var next = technicians[(currentIndex + 1) % technicians.Count];
        if (next == sla.Ticket.AssignedTechnicianId) return;
        dbContext.TicketAssignmentHistory.Add(new TicketAssignmentHistory
        {
            Id = Guid.CreateVersion7(), TicketId = sla.TicketId, QueueId = queue.Id,
            FromTechnicianId = sla.Ticket.AssignedTechnicianId, ToTechnicianId = next,
            Kind = AssignmentKind.Automatic, ActorId = "system:sla-scheduler", OccurredAt = now,
        });
        sla.Ticket.AssignedTechnicianId = next;
        queue.LastAssignedTechnicianId = next;
        await notificationRouter.RouteAsync(
            new NotificationEnvelope(
                "SlaEscalated",
                NotificationSeverity.Warning,
                $"SLA escalation for {sla.Ticket.Number}",
                "The ticket was reassigned after an SLA breach.",
                DeepLink(sla.TicketId),
                DedupeKey: $"ticket:{sla.TicketId}:sla-escalated",
                Facts:
                [
                    new NotificationFact("Ticket", sla.Ticket.Number),
                    new NotificationFact("Reassigned to", next),
                ]),
            // The person who has just been handed it — the one notification in this file addressed to
            // an individual rather than announced to a team.
            [next],
            cancellationToken);
    }

    /// <summary>
    /// One SLA notification, routed rather than emailed directly (WP-3.10). The technician who holds
    /// the ticket (or, unassigned, the requester) is named so their own preference applies; every
    /// routing rule that matches also fires, which is how an operations channel hears about a breach
    /// nobody is assigned to.
    /// </summary>
    private async Task NotifySlaAsync(
        TicketSla sla,
        string eventKind,
        NotificationSeverity severity,
        string subject,
        string body,
        string targetName,
        DateTimeOffset dueAt,
        CancellationToken cancellationToken)
    {
        var recipient = sla.Ticket.AssignedTechnicianId ?? sla.Ticket.RequesterId;
        await notificationRouter.RouteAsync(
            new NotificationEnvelope(
                eventKind,
                severity,
                subject,
                body,
                DeepLink(sla.TicketId),
                // No device group: an SLA is not about a device, so a rule that names one is
                // deliberately not matched.
                DedupeKey: $"ticket:{sla.TicketId}:{eventKind}:{targetName}",
                Facts:
                [
                    new NotificationFact("Ticket", sla.Ticket.Number),
                    new NotificationFact("Target", targetName),
                    new NotificationFact("Due at", $"{dueAt:u}"),
                    new NotificationFact("Policy", sla.Policy.Name),
                ]),
            recipient is null ? null : [recipient],
            cancellationToken);
    }

    private string? DeepLink(Guid ticketId)
    {
        var baseUrl = notificationOptions.Value.DeepLinkBaseUrl;
        return string.IsNullOrWhiteSpace(baseUrl) ? null : $"{baseUrl.TrimEnd('/')}/tickets/{ticketId}";
    }

    // The SLA arithmetic itself lives in SlaClock, so the blast-radius read (WP-5.2) asks the same
    // question of a whole outage that this file asks of one ticket without a second copy of it.
    private static double CurrentElapsedSeconds(TicketSla sla, DateTimeOffset now) =>
        SlaClock.ElapsedSeconds(sla, now);

    private static void AccumulateActive(TicketSla sla, DateTimeOffset now)
    {
        if (sla.ActiveSince is null) return;
        sla.AccumulatedBusinessSeconds += BusinessTimeCalculator.Elapsed(sla.ActiveSince.Value, now, sla.Calendar).TotalSeconds;
        sla.ActiveSince = null;
    }

    private static DateTimeOffset DueAt(TicketSla sla, DateTimeOffset now, double targetSeconds) =>
        SlaClock.DueAt(sla, now, targetSeconds);

    private static SlaRemainingResponse MapRemaining(TicketSla sla, DateTimeOffset now)
    {
        var current = CurrentElapsedSeconds(sla, now);
        var responseElapsed = sla.ResponseBusinessSeconds ?? current;
        // Snapshotted onto the ticket, not read through the policy — see TicketSla.
        var responseTarget = sla.ResponseTargetMinutes * 60d;
        var resolutionTarget = sla.ResolutionTargetMinutes * 60d;
        return new(sla.TicketId, sla.Policy.Name, sla.ActiveSince is null && sla.ResolutionCompletedAt is null,
            Math.Max(0, responseTarget - responseElapsed), Math.Max(0, resolutionTarget - current),
            DueAt(sla, now, responseTarget), DueAt(sla, now, resolutionTarget),
            sla.ResponseCompletedAt, sla.ResolutionCompletedAt);
    }

    private static string ActorId(ClaimsPrincipal actor) => actor.FindFirstValue("sub")
        ?? actor.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
    private static BusinessHoursCalendarResponse Map(BusinessHoursCalendar item) =>
        new(item.Id, item.Name, item.TimeZoneId, item.WorkingDays, item.StartTime, item.EndTime);
    private static SlaPolicyResponse Map(SlaPolicy item, int ticketCount) => new(
        item.Id,
        item.Name,
        item.SortOrder,
        item.Priority,
        item.TicketType,
        item.CategoryId,
        item.Category?.Name,
        item.ResponseTargetMinutes,
        item.ResolutionTargetMinutes,
        item.WarningPercent,
        item.CalendarId,
        item.Calendar?.Name ?? string.Empty,
        item.IsActive,
        ticketCount);
}
