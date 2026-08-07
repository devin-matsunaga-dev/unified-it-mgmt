using System.Security.Claims;

using Microsoft.EntityFrameworkCore;
using Modules.Helpdesk.Data;
using Modules.Helpdesk.Features.Tickets;
using Platform.Auditing;

namespace Modules.Helpdesk.Features.Assignments;

public sealed class AssignmentService(HelpdeskDbContext dbContext, IAuditService auditService) : IAssignmentService
{
    public async Task<TeamResponse> CreateTeamAsync(
        CreateTeamRequest request, ClaimsPrincipal actor, CancellationToken cancellationToken)
    {
        var team = new Team { Id = Guid.CreateVersion7(), Name = request.Name.Trim(), CreatedAt = DateTimeOffset.UtcNow };
        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync(cancellationToken);
        var response = new TeamResponse(team.Id, team.Name);
        await auditService.WriteAsync(actor, "Created", "Team", team.Id.ToString(), null, response, cancellationToken);
        return response;
    }

    public async Task<bool> AddTeamMemberAsync(
        Guid teamId, AddTeamMemberRequest request, ClaimsPrincipal actor, CancellationToken cancellationToken)
    {
        if (!await dbContext.Teams.AnyAsync(team => team.Id == teamId, cancellationToken))
        {
            return false;
        }

        var technicianId = request.TechnicianId.Trim();
        if (!await dbContext.TeamMembers.AnyAsync(
                member => member.TeamId == teamId && member.TechnicianId == technicianId, cancellationToken))
        {
            dbContext.TeamMembers.Add(new TeamMember
            {
                TeamId = teamId,
                TechnicianId = technicianId,
                AddedAt = DateTimeOffset.UtcNow,
            });
            await dbContext.SaveChangesAsync(cancellationToken);
            await auditService.WriteAsync(
                actor, "MemberAdded", "Team", teamId.ToString(), null,
                new { TechnicianId = technicianId }, cancellationToken);
        }

        return true;
    }

    public async Task<QueueResponse?> CreateQueueAsync(
        CreateQueueRequest request, ClaimsPrincipal actor, CancellationToken cancellationToken)
    {
        if (!await dbContext.Teams.AnyAsync(team => team.Id == request.TeamId, cancellationToken))
        {
            return null;
        }

        var queue = new TicketQueue
        {
            Id = Guid.CreateVersion7(), Name = request.Name.Trim(), TeamId = request.TeamId, CreatedAt = DateTimeOffset.UtcNow,
        };
        dbContext.TicketQueues.Add(queue);
        await dbContext.SaveChangesAsync(cancellationToken);
        var response = new QueueResponse(queue.Id, queue.Name, queue.TeamId);
        await auditService.WriteAsync(actor, "Created", "TicketQueue", queue.Id.ToString(), null, response, cancellationToken);
        return response;
    }

    public async Task<IReadOnlyList<QueueResponse>> ListQueuesAsync(CancellationToken cancellationToken) =>
        await dbContext.TicketQueues.OrderBy(queue => queue.Name).ThenBy(queue => queue.Id)
            .Select(queue => new QueueResponse(queue.Id, queue.Name, queue.TeamId))
            .ToListAsync(cancellationToken);

    public async Task<QueuePlacementResult> PlaceInQueueAsync(
        Guid ticketId, PlaceTicketInQueueRequest request, ClaimsPrincipal actor, CancellationToken cancellationToken)
    {
        var ticket = await VisibleTickets(actor).SingleOrDefaultAsync(item => item.Id == ticketId, cancellationToken);
        if (ticket is null)
        {
            return new(QueuePlacementOutcome.TicketNotFound);
        }

        var queue = await dbContext.TicketQueues.Include(item => item.Team).ThenInclude(team => team.Members)
            .SingleOrDefaultAsync(item => item.Id == request.QueueId, cancellationToken);
        if (queue is null)
        {
            return new(QueuePlacementOutcome.QueueNotFound);
        }

        var before = TicketService.Map(ticket);
        var previousTechnicianId = ticket.AssignedTechnicianId;
        var technicians = queue.Team.Members.Select(member => member.TechnicianId)
            .OrderBy(id => id, StringComparer.Ordinal).ToList();
        string? assignedTechnicianId = null;
        if (technicians.Count > 0)
        {
            var lastIndex = queue.LastAssignedTechnicianId is null
                ? -1
                : technicians.FindIndex(id => id == queue.LastAssignedTechnicianId);
            assignedTechnicianId = technicians[(lastIndex + 1) % technicians.Count];
            queue.LastAssignedTechnicianId = assignedTechnicianId;
        }

        var occurredAt = DateTimeOffset.UtcNow;
        ticket.QueueId = queue.Id;
        ticket.Queue = queue;
        ticket.AssignedTechnicianId = assignedTechnicianId;
        ticket.UpdatedAt = occurredAt;
        if (assignedTechnicianId is not null)
        {
            dbContext.TicketAssignmentHistory.Add(new TicketAssignmentHistory
            {
                Id = Guid.CreateVersion7(), TicketId = ticket.Id, QueueId = queue.Id,
                FromTechnicianId = previousTechnicianId, ToTechnicianId = assignedTechnicianId,
                Kind = AssignmentKind.Automatic, ActorId = ActorId(actor), OccurredAt = occurredAt,
            });
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        var after = TicketService.Map(ticket);
        await auditService.WriteAsync(actor, "QueueChanged", "Ticket", ticket.Id.ToString(), before, after, cancellationToken);
        return new(QueuePlacementOutcome.Success, after);
    }

    public async Task<AssignmentResult> AssignAsync(
        Guid ticketId, AssignTicketRequest request, ClaimsPrincipal actor, CancellationToken cancellationToken)
    {
        var ticket = await VisibleTickets(actor).SingleOrDefaultAsync(item => item.Id == ticketId, cancellationToken);
        if (ticket is null)
        {
            return new(AssignmentOutcome.TicketNotFound);
        }

        if (ticket.QueueId is null)
        {
            return new(AssignmentOutcome.TicketHasNoQueue, Error: "The ticket is not in a queue.");
        }

        var technicianId = request.TechnicianId.Trim();
        var isMember = await dbContext.TicketQueues.AnyAsync(
            queue => queue.Id == ticket.QueueId && queue.Team.Members.Any(member => member.TechnicianId == technicianId),
            cancellationToken);
        if (!isMember)
        {
            return new(AssignmentOutcome.TechnicianNotInQueueTeam,
                Error: "The technician is not a member of the queue's team.");
        }

        var occurredAt = DateTimeOffset.UtcNow;
        var before = TicketService.Map(ticket);
        dbContext.TicketAssignmentHistory.Add(new TicketAssignmentHistory
        {
            Id = Guid.CreateVersion7(), TicketId = ticket.Id, QueueId = ticket.QueueId.Value,
            FromTechnicianId = ticket.AssignedTechnicianId, ToTechnicianId = technicianId,
            Kind = AssignmentKind.Manual, ActorId = ActorId(actor), OccurredAt = occurredAt,
        });
        ticket.AssignedTechnicianId = technicianId;
        ticket.UpdatedAt = occurredAt;
        await dbContext.SaveChangesAsync(cancellationToken);

        var after = TicketService.Map(ticket);
        await auditService.WriteAsync(actor, "Assigned", "Ticket", ticket.Id.ToString(), before, after, cancellationToken);
        return new(AssignmentOutcome.Success, after);
    }

    public async Task<IReadOnlyList<EligibleTechnicianResponse>?> GetEligibleTechniciansAsync(
        Guid ticketId, ClaimsPrincipal actor, CancellationToken cancellationToken)
    {
        var ticket = await VisibleTickets(actor)
            .Select(item => new { item.Id, item.QueueId })
            .SingleOrDefaultAsync(item => item.Id == ticketId, cancellationToken);
        if (ticket is null)
        {
            return null;
        }

        if (ticket.QueueId is null)
        {
            return [];
        }

        return await dbContext.TicketQueues
            .Where(queue => queue.Id == ticket.QueueId)
            .SelectMany(queue => queue.Team.Members)
            .OrderBy(member => member.TechnicianId)
            .Select(member => new EligibleTechnicianResponse(member.TechnicianId))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<TicketAssignmentResponse>?> GetHistoryAsync(
        Guid ticketId, ClaimsPrincipal actor, CancellationToken cancellationToken)
    {
        if (!await VisibleTickets(actor).AnyAsync(ticket => ticket.Id == ticketId, cancellationToken))
        {
            return null;
        }

        return await dbContext.TicketAssignmentHistory.Where(history => history.TicketId == ticketId)
            .OrderBy(history => history.OccurredAt).ThenBy(history => history.Id)
            .Select(history => new TicketAssignmentResponse(
                history.Id, history.TicketId, history.QueueId, history.FromTechnicianId,
                history.ToTechnicianId, history.Kind, history.ActorId, history.OccurredAt))
            .ToListAsync(cancellationToken);
    }

    public async Task<TicketPageResponse> ListMineAsync(
        int page, int pageSize, ClaimsPrincipal actor, CancellationToken cancellationToken)
    {
        var actorId = ActorId(actor);
        var query = VisibleTickets(actor)
            .Where(ticket => ticket.AssignedTechnicianId == actorId);
        var total = await query.CountAsync(cancellationToken);
        var tickets = await query.OrderByDescending(ticket => ticket.CreatedAt)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        return new(tickets.Select(TicketService.Map).ToList(), total, page, pageSize);
    }

    private static string ActorId(ClaimsPrincipal actor) =>
        actor.FindFirstValue("sub") ?? actor.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException("An authenticated actor identifier is required.");

    private IQueryable<Ticket> VisibleTickets(ClaimsPrincipal actor)
    {
        var query = dbContext.Tickets.Include(ticket => ticket.Status).Include(ticket => ticket.Queue).AsQueryable();
        return actor.IsInRole("EndUser")
            ? query.Where(ticket => ticket.RequesterId == ActorId(actor))
            : query;
    }
}
