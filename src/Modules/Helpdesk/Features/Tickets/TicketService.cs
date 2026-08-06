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
            RequesterId = requesterId,
            CreatedAt = now,
            UpdatedAt = now,
        };

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        dbContext.Tickets.Add(ticket);
        await dbContext.SaveChangesAsync(cancellationToken);
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

    private IQueryable<Ticket> VisibleTickets(ClaimsPrincipal actor)
    {
        var query = dbContext.Tickets.AsQueryable();
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
        ticket.RequesterId,
        ticket.CreatedAt,
        ticket.UpdatedAt);
}
