using System.Security.Claims;
using System.Net.Mail;

using Contracts.Events;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Modules.Helpdesk.Data;
using Platform.Auditing;
using Platform.Notifications;
using Modules.Helpdesk.Features.Sla;

namespace Modules.Helpdesk.Features.Tickets;

public sealed class TicketService(
    HelpdeskDbContext dbContext,
    IPublishEndpoint publishEndpoint,
    IAuditService auditService,
    ISlaService slaService,
    INotificationService notificationService) : ITicketService
{
    public async Task<TicketResponse?> CreateAsync(
        CreateTicketRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var requesterId = IsEndUser(actor) ? GetActorId(actor) : request.RequesterId ?? GetActorId(actor);
        var requesterName = request.RequesterId is null || IsEndUser(actor)
            ? GetActorDisplayName(actor)
            : request.RequesterId;
        var requesterEmail = ValidEmailOrNull(request.RequesterId)
            ?? (request.RequesterId is null || IsEndUser(actor) ? GetActorEmail(actor) : null);
        TicketQueue? queue = null;
        string? assignedTechnicianId = null;
        if (request.QueueId is not null)
        {
            queue = await dbContext.TicketQueues.Include(item => item.Team).ThenInclude(team => team.Members)
                .SingleOrDefaultAsync(item => item.Id == request.QueueId, cancellationToken);
            if (queue is null)
            {
                return null;
            }

            var technicians = queue.Team.Members.Select(member => member.TechnicianId)
                .OrderBy(id => id, StringComparer.Ordinal).ToList();
            if (technicians.Count > 0)
            {
                var lastIndex = queue.LastAssignedTechnicianId is null
                    ? -1
                    : technicians.FindIndex(id => id == queue.LastAssignedTechnicianId);
                assignedTechnicianId = technicians[(lastIndex + 1) % technicians.Count];
                queue.LastAssignedTechnicianId = assignedTechnicianId;
            }
        }

        var ticket = new Ticket
        {
            Id = Guid.CreateVersion7(),
            Title = request.Title.Trim(),
            Description = request.Description.Trim(),
            Type = request.Type,
            Urgency = request.Urgency,
            Impact = request.Impact,
            Priority = TicketPriorityMatrix.Calculate(request.Urgency, request.Impact),
            StatusId = DefaultTicketStatuses.NewId,
            RequesterId = requesterId,
            RequesterDisplayName = requesterName,
            RequesterEmail = requesterEmail,
            QueueId = queue?.Id,
            Queue = queue,
            AssignedTechnicianId = assignedTechnicianId,
            CreatedAt = now,
            UpdatedAt = now,
        };

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        dbContext.Tickets.Add(ticket);
        if (queue is not null && assignedTechnicianId is not null)
        {
            dbContext.TicketAssignmentHistory.Add(new TicketAssignmentHistory
            {
                Id = Guid.CreateVersion7(), TicketId = ticket.Id, QueueId = queue.Id,
                ToTechnicianId = assignedTechnicianId, Kind = AssignmentKind.Automatic,
                ActorId = GetActorId(actor), OccurredAt = now,
            });
        }
        await dbContext.SaveChangesAsync(cancellationToken);
        await dbContext.Entry(ticket).Reference(item => item.Status).LoadAsync(cancellationToken);
        await slaService.StartAsync(ticket, now, cancellationToken);
        await publishEndpoint.Publish(new TicketCreated(
            Guid.CreateVersion7(), now, ticket.Id, ticket.Number, ticket.RequesterId,
            ticket.Type.ToString(), ticket.Priority.ToString()), cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var response = Map(ticket);
        await auditService.WriteAsync(actor, "Created", "Ticket", ticket.Id.ToString(), null, response, cancellationToken);
        await NotifyAsync(ticket, "TicketCreated", "created", cancellationToken);
        return response;
    }

    public async Task<TicketResponse?> GetAsync(
        Guid id,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        var ticket = await VisibleTickets(actor).SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        return ticket is null ? null : Map(ticket);
    }

    public async Task<TicketPageResponse> ListAsync(
        int page,
        int pageSize,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        var query = VisibleTickets(actor);
        var total = await query.CountAsync(cancellationToken);
        var tickets = await query.OrderByDescending(ticket => ticket.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
        return new TicketPageResponse(tickets.Select(Map).ToList(), total, page, pageSize);
    }

    public async Task<TicketResponse?> UpdateAsync(
        Guid id,
        UpdateTicketRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        var ticket = await VisibleTickets(actor).SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (ticket is null)
        {
            return null;
        }

        var before = Map(ticket);
        ticket.Title = request.Title.Trim();
        ticket.Description = request.Description.Trim();
        ticket.Type = request.Type;
        ticket.Urgency = request.Urgency;
        ticket.Impact = request.Impact;
        ticket.Priority = TicketPriorityMatrix.Calculate(request.Urgency, request.Impact);
        ticket.UpdatedAt = DateTimeOffset.UtcNow;

        await publishEndpoint.Publish(new TicketUpdated(
            Guid.CreateVersion7(), ticket.UpdatedAt, ticket.Id, ticket.Number, ticket.RequesterId,
            ticket.Type.ToString(), ticket.Priority.ToString()), cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);

        var after = Map(ticket);
        await auditService.WriteAsync(actor, "Updated", "Ticket", ticket.Id.ToString(), before, after, cancellationToken);
        await NotifyAsync(ticket, "TicketUpdated", "updated", cancellationToken);
        return after;
    }

    public async Task<TransitionTicketResult> TransitionAsync(
        Guid id,
        TransitionTicketRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        var ticket = await VisibleTickets(actor).SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (ticket is null)
        {
            return new(TransitionTicketOutcome.NotFound);
        }

        var targetStatus = await dbContext.TicketStatuses.SingleOrDefaultAsync(
            status => status.Name.ToLower() == request.TargetStatus.Trim().ToLower(), cancellationToken);
        if (targetStatus is null)
        {
            return new(TransitionTicketOutcome.UnknownStatus, Error: $"Status '{request.TargetStatus}' does not exist.");
        }

        if (IsEndUser(actor) && !(ticket.StatusId == DefaultTicketStatuses.ResolvedId
                && targetStatus.Id == DefaultTicketStatuses.ClosedId))
        {
            return new(
                TransitionTicketOutcome.Forbidden,
                Error: "Requesters may only close a resolved ticket.");
        }

        var allowed = await dbContext.TicketStatusTransitions.AnyAsync(
            transition => transition.FromStatusId == ticket.StatusId && transition.ToStatusId == targetStatus.Id,
            cancellationToken);
        if (!allowed)
        {
            return new(
                TransitionTicketOutcome.IllegalTransition,
                Error: $"Transition from '{ticket.Status.Name}' to '{targetStatus.Name}' is not allowed.");
        }

        var resolutionNote = string.IsNullOrWhiteSpace(request.ResolutionNote) ? null : request.ResolutionNote.Trim();
        if (targetStatus.RequiresResolutionNote && resolutionNote is null)
        {
            return new(
                TransitionTicketOutcome.ResolutionNoteRequired,
                Error: $"A resolution note is required when transitioning to '{targetStatus.Name}'.");
        }

        var actorId = GetActorId(actor);
        var occurredAt = DateTimeOffset.UtcNow;
        var before = Map(ticket);
        var fromStatusId = ticket.StatusId;
        var history = new TicketTransitionHistory
        {
            Id = Guid.CreateVersion7(),
            TicketId = ticket.Id,
            FromStatusId = ticket.StatusId,
            ToStatusId = targetStatus.Id,
            ResolutionNote = resolutionNote,
            ActorId = actorId,
            OccurredAt = occurredAt,
        };

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        dbContext.TicketTransitionHistory.Add(history);
        ticket.StatusId = targetStatus.Id;
        ticket.Status = targetStatus;
        ticket.UpdatedAt = occurredAt;
        await slaService.RecordStatusChangeAsync(ticket, fromStatusId, occurredAt, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await publishEndpoint.Publish(new TicketStatusChanged(
            Guid.CreateVersion7(), occurredAt, ticket.Id, ticket.Number,
            before.Status, targetStatus.Name, actorId), cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var after = Map(ticket);
        await auditService.WriteAsync(
            actor, "StatusChanged", "Ticket", ticket.Id.ToString(), before, after, cancellationToken);
        if (targetStatus.Name.Equals("Resolved", StringComparison.OrdinalIgnoreCase))
        {
            await NotifyAsync(ticket, "TicketResolved", "resolved", cancellationToken);
        }
        return new(TransitionTicketOutcome.Success, after);
    }

    public async Task<IReadOnlyList<TicketTransitionResponse>?> GetTransitionHistoryAsync(
        Guid id,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        if (!await VisibleTickets(actor).AnyAsync(ticket => ticket.Id == id, cancellationToken))
        {
            return null;
        }

        return await dbContext.TicketTransitionHistory
            .Where(history => history.TicketId == id)
            .OrderBy(history => history.OccurredAt)
            .ThenBy(history => history.Id)
            .Select(history => new TicketTransitionResponse(
                history.Id,
                history.TicketId,
                history.FromStatus.Name,
                history.ToStatus.Name,
                history.ResolutionNote,
                history.ActorId,
                history.OccurredAt))
            .ToListAsync(cancellationToken);
    }

    private IQueryable<Ticket> VisibleTickets(ClaimsPrincipal actor)
    {
        var query = dbContext.Tickets.Include(ticket => ticket.Status).Include(ticket => ticket.Queue).AsQueryable();
        return IsEndUser(actor) ? query.Where(ticket => ticket.RequesterId == GetActorId(actor)) : query;
    }

    private static bool IsEndUser(ClaimsPrincipal actor) => actor.IsInRole("EndUser");

    private static string GetActorId(ClaimsPrincipal actor) =>
        actor.FindFirstValue("sub") ?? actor.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException("An authenticated actor identifier is required.");

    private static string GetActorDisplayName(ClaimsPrincipal actor) =>
        actor.FindFirstValue("name") ?? actor.Identity?.Name ?? actor.FindFirstValue("preferred_username")
        ?? GetActorId(actor);

    private static string? GetActorEmail(ClaimsPrincipal actor) =>
        ValidEmailOrNull(actor.FindFirstValue(ClaimTypes.Email) ?? actor.FindFirstValue("email"));

    private static string? ValidEmailOrNull(string? value) =>
        MailAddress.TryCreate(value, out var address) ? address.Address : null;

    private Task NotifyAsync(Ticket ticket, string templateName, string action, CancellationToken cancellationToken) =>
        notificationService.SendAsync(new NotificationMessage(
            ticket.RequesterEmail ?? string.Empty,
            new NotificationTemplate(
                templateName,
                $"[{ticket.Number}] Ticket {action}: {ticket.Title}",
                $"Your ticket {ticket.Number} has been {action}. Reply to this email to add a comment."),
            new { TicketId = ticket.Id, TicketNumber = ticket.Number, ticket.Title },
            new Dictionary<string, string>
            {
                ["Message-Id"] = $"<ticket-{ticket.Id:N}@it-platform.local>",
                ["X-IT-Platform-Ticket-Id"] = ticket.Id.ToString(),
            }), cancellationToken);

    internal static TicketResponse Map(Ticket ticket) => new(
        ticket.Id,
        ticket.Number,
        ticket.Title,
        ticket.Description,
        ticket.Type,
        ticket.Urgency,
        ticket.Impact,
        ticket.Priority,
        ticket.Status.Name,
        ticket.RequesterId,
        ticket.RequesterDisplayName ?? ticket.RequesterId,
        ticket.QueueId,
        ticket.Queue?.Name,
        ticket.AssignedTechnicianId,
        ticket.CreatedAt,
        ticket.UpdatedAt);
}
