using System.Security.Claims;

using Contracts.Events;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Modules.Helpdesk.Data;
using Platform.Auditing;
using Platform.Notifications;

namespace Modules.Helpdesk.Features.Sla;

public sealed class SlaService(
    HelpdeskDbContext dbContext,
    IPublishEndpoint publishEndpoint,
    INotificationService notificationService,
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

    public async Task<SlaPolicyResponse?> CreatePolicyAsync(
        CreateSlaPolicyRequest request, ClaimsPrincipal actor, CancellationToken cancellationToken)
    {
        if (!await dbContext.BusinessHoursCalendars.AnyAsync(item => item.Id == request.CalendarId, cancellationToken)) return null;
        var policy = new SlaPolicy
        {
            Id = Guid.CreateVersion7(), Name = request.Name.Trim(), Priority = request.Priority,
            Category = string.IsNullOrWhiteSpace(request.Category) ? null : request.Category.Trim(),
            ResponseTargetMinutes = request.ResponseTargetMinutes,
            ResolutionTargetMinutes = request.ResolutionTargetMinutes, WarningPercent = request.WarningPercent,
            CalendarId = request.CalendarId, CreatedAt = DateTimeOffset.UtcNow,
        };
        dbContext.SlaPolicies.Add(policy);
        await dbContext.SaveChangesAsync(cancellationToken);
        var response = Map(policy);
        await auditService.WriteAsync(actor, "Created", "SlaPolicy", policy.Id.ToString(), null, response, cancellationToken);
        return response;
    }

    public async Task StartAsync(Ticket ticket, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var policy = await dbContext.SlaPolicies.Include(item => item.Calendar)
            .Where(item => item.IsActive && item.Priority == ticket.Priority && item.Category == null)
            .OrderBy(item => item.CreatedAt).FirstOrDefaultAsync(cancellationToken);
        if (policy is null) return;
        dbContext.TicketSlas.Add(new TicketSla
        {
            Id = Guid.CreateVersion7(), TicketId = ticket.Id, PolicyId = policy.Id,
            StartedAt = now, ActiveSince = now,
        });
    }

    public async Task RecordStatusChangeAsync(
        Ticket ticket, Guid fromStatusId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var sla = await dbContext.TicketSlas.Include(item => item.Policy).ThenInclude(item => item.Calendar)
            .SingleOrDefaultAsync(item => item.TicketId == ticket.Id, cancellationToken);
        if (sla is null) return;
        if (ticket.StatusId == DefaultTicketStatuses.PendingId && sla.ActiveSince is not null)
        {
            sla.AccumulatedBusinessSeconds += BusinessTimeCalculator.Elapsed(sla.ActiveSince.Value, now, sla.Policy.Calendar).TotalSeconds;
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
        var sla = await dbContext.TicketSlas.Include(item => item.Policy).ThenInclude(item => item.Calendar)
            .SingleOrDefaultAsync(item => item.TicketId == ticketId, cancellationToken);
        if (sla is null || sla.ResponseCompletedAt is not null) return;
        sla.ResponseBusinessSeconds = CurrentElapsedSeconds(sla, now);
        sla.ResponseCompletedAt = now;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<SlaRemainingResponse?> GetRemainingAsync(
        Guid ticketId, ClaimsPrincipal actor, CancellationToken cancellationToken)
    {
        var sla = await dbContext.TicketSlas.Include(item => item.Ticket).Include(item => item.Policy).ThenInclude(item => item.Calendar)
            .SingleOrDefaultAsync(item => item.TicketId == ticketId, cancellationToken);
        if (sla is null || (actor.IsInRole("EndUser") && sla.Ticket.RequesterId != ActorId(actor))) return null;
        return MapRemaining(sla, DateTimeOffset.UtcNow);
    }

    public async Task EvaluateAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var slas = await dbContext.TicketSlas.Include(item => item.Ticket).ThenInclude(item => item.Queue!).ThenInclude(item => item.Team).ThenInclude(item => item.Members)
            .Include(item => item.Policy).ThenInclude(item => item.Calendar)
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
        var target = (response ? sla.Policy.ResponseTargetMinutes : sla.Policy.ResolutionTargetMinutes) * 60d;
        var warned = response ? sla.ResponseWarningRaised : sla.ResolutionWarningRaised;
        var breached = response ? sla.ResponseBreached : sla.ResolutionBreached;
        var targetName = response ? "Response" : "Resolution";
        var dueAt = DueAt(sla, now, target);
        if (!warned && elapsed >= target * sla.Policy.WarningPercent / 100d)
        {
            if (response) sla.ResponseWarningRaised = true; else sla.ResolutionWarningRaised = true;
            await publishEndpoint.Publish(new SlaWarningRaised(Guid.CreateVersion7(), now, sla.TicketId, sla.Ticket.Number, targetName, dueAt), cancellationToken);
            await notificationService.SendAsync(new NotificationMessage(
                sla.Ticket.AssignedTechnicianId ?? sla.Ticket.RequesterId,
                new NotificationTemplate("SlaWarning", $"SLA warning for {sla.Ticket.Number}", "{{Target}} target is approaching."),
                new { sla.TicketId, Target = targetName, DueAt = dueAt }), cancellationToken);
            await auditService.WriteAsync(SchedulerActor, "WarningRaised", "TicketSla", sla.Id.ToString(), null, new { Target = targetName, DueAt = dueAt }, cancellationToken);
        }
        if (!breached && elapsed >= target)
        {
            if (response) sla.ResponseBreached = true; else sla.ResolutionBreached = true;
            await publishEndpoint.Publish(new SlaBreached(Guid.CreateVersion7(), now, sla.TicketId, sla.Ticket.Number, targetName, dueAt), cancellationToken);
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
        await notificationService.SendAsync(new NotificationMessage(next,
            new NotificationTemplate("SlaEscalation", $"SLA escalation for {sla.Ticket.Number}", "The ticket was reassigned after an SLA breach."),
            new { sla.TicketId }), cancellationToken);
    }

    private static double CurrentElapsedSeconds(TicketSla sla, DateTimeOffset now) =>
        sla.AccumulatedBusinessSeconds + (sla.ActiveSince is null ? 0 : BusinessTimeCalculator.Elapsed(sla.ActiveSince.Value, now, sla.Policy.Calendar).TotalSeconds);

    private static void AccumulateActive(TicketSla sla, DateTimeOffset now)
    {
        if (sla.ActiveSince is null) return;
        sla.AccumulatedBusinessSeconds += BusinessTimeCalculator.Elapsed(sla.ActiveSince.Value, now, sla.Policy.Calendar).TotalSeconds;
        sla.ActiveSince = null;
    }

    private static DateTimeOffset DueAt(TicketSla sla, DateTimeOffset now, double targetSeconds)
    {
        var remaining = Math.Max(0, targetSeconds - sla.AccumulatedBusinessSeconds);
        return BusinessTimeCalculator.Add(sla.ActiveSince ?? now, TimeSpan.FromSeconds(remaining), sla.Policy.Calendar);
    }

    private static SlaRemainingResponse MapRemaining(TicketSla sla, DateTimeOffset now)
    {
        var current = CurrentElapsedSeconds(sla, now);
        var responseElapsed = sla.ResponseBusinessSeconds ?? current;
        var responseTarget = sla.Policy.ResponseTargetMinutes * 60d;
        var resolutionTarget = sla.Policy.ResolutionTargetMinutes * 60d;
        return new(sla.TicketId, sla.Policy.Name, sla.ActiveSince is null && sla.ResolutionCompletedAt is null,
            Math.Max(0, responseTarget - responseElapsed), Math.Max(0, resolutionTarget - current),
            DueAt(sla, now, responseTarget), DueAt(sla, now, resolutionTarget),
            sla.ResponseCompletedAt, sla.ResolutionCompletedAt);
    }

    private static string ActorId(ClaimsPrincipal actor) => actor.FindFirstValue("sub")
        ?? actor.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
    private static BusinessHoursCalendarResponse Map(BusinessHoursCalendar item) =>
        new(item.Id, item.Name, item.TimeZoneId, item.WorkingDays, item.StartTime, item.EndTime);
    private static SlaPolicyResponse Map(SlaPolicy item) => new(item.Id, item.Name, item.Priority, item.Category,
        item.ResponseTargetMinutes, item.ResolutionTargetMinutes, item.WarningPercent, item.CalendarId);
}
