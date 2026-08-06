using System.Security.Claims;

using Contracts.Events;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Modules.Helpdesk.Data;
using Platform.Auditing;

namespace Modules.Helpdesk.Features.Tickets;

public sealed class TicketService(
    HelpdeskDbContext dbContext,
    IPublishEndpoint publishEndpoint,
    IAuditService auditService) : ITicketService
{
    public async Task<TicketResponse> CreateAsync(
        CreateTicketRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var requesterId = IsEndUser(actor) ? GetActorId(actor) : request.RequesterId ?? GetActorId(actor);
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
            CreatedAt = now,
            UpdatedAt = now,
        };

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        dbContext.Tickets.Add(ticket);
        await dbContext.SaveChangesAsync(cancellationToken);
        await dbContext.Entry(ticket).Reference(item => item.Status).LoadAsync(cancellationToken);
        await publishEndpoint.Publish(new TicketCreated(
            Guid.CreateVersion7(), now, ticket.Id, ticket.Number, ticket.RequesterId,
            ticket.Type.ToString(), ticket.Priority.ToString()), cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var response = Map(ticket);
        await auditService.WriteAsync(actor, "Created", "Ticket", ticket.Id.ToString(), null, response, cancellationToken);
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
        await dbContext.SaveChangesAsync(cancellationToken);
        await publishEndpoint.Publish(new TicketStatusChanged(
            Guid.CreateVersion7(), occurredAt, ticket.Id, ticket.Number,
            before.Status, targetStatus.Name, actorId), cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var after = Map(ticket);
        await auditService.WriteAsync(
            actor, "StatusChanged", "Ticket", ticket.Id.ToString(), before, after, cancellationToken);
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
        var query = dbContext.Tickets.Include(ticket => ticket.Status).AsQueryable();
        return IsEndUser(actor) ? query.Where(ticket => ticket.RequesterId == GetActorId(actor)) : query;
    }

    private static bool IsEndUser(ClaimsPrincipal actor) => actor.IsInRole("EndUser");

    private static string GetActorId(ClaimsPrincipal actor) =>
        actor.FindFirstValue("sub") ?? actor.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException("An authenticated actor identifier is required.");

    private static TicketResponse Map(Ticket ticket) => new(
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
        ticket.CreatedAt,
        ticket.UpdatedAt);
}
