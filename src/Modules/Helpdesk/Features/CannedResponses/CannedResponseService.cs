using System.Security.Claims;

using Microsoft.EntityFrameworkCore;
using Modules.Helpdesk.Data;
using Platform.Auditing;

namespace Modules.Helpdesk.Features.CannedResponses;

public sealed class CannedResponseService(HelpdeskDbContext dbContext, IAuditService auditService)
    : ICannedResponseService
{
    public async Task<IReadOnlyList<CannedResponseResponse>> ListAsync(
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        if (IsEndUser(actor))
        {
            return [];
        }

        var responses = await dbContext.CannedResponses
            .OrderBy(response => response.Name)
            .ToListAsync(cancellationToken);
        return [.. responses.Select(Map)];
    }

    public async Task<CannedResponseResult> CreateAsync(
        SaveCannedResponseRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        if (IsEndUser(actor))
        {
            return new(CannedResponseOutcome.Forbidden, Error: "Requesters cannot manage canned responses.");
        }

        var name = request.Name.Trim();
        if (await NameTakenAsync(name, null, cancellationToken))
        {
            return new(CannedResponseOutcome.DuplicateName, Error: $"A canned response named '{name}' already exists.");
        }

        var now = DateTimeOffset.UtcNow;
        var response = new CannedResponse
        {
            Id = Guid.CreateVersion7(),
            Name = name,
            Body = request.Body.Trim(),
            CreatedById = ActorId(actor),
            CreatedAt = now,
            UpdatedAt = now,
        };
        dbContext.CannedResponses.Add(response);
        await dbContext.SaveChangesAsync(cancellationToken);

        var mapped = Map(response);
        await auditService.WriteAsync(
            actor, "Created", "CannedResponse", response.Id.ToString(), null, mapped, cancellationToken);
        return new(CannedResponseOutcome.Success, mapped);
    }

    public async Task<CannedResponseResult> UpdateAsync(
        Guid id,
        SaveCannedResponseRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        if (IsEndUser(actor))
        {
            return new(CannedResponseOutcome.Forbidden, Error: "Requesters cannot manage canned responses.");
        }

        var response = await dbContext.CannedResponses.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (response is null)
        {
            return new(CannedResponseOutcome.NotFound);
        }

        var name = request.Name.Trim();
        if (await NameTakenAsync(name, id, cancellationToken))
        {
            return new(CannedResponseOutcome.DuplicateName, Error: $"A canned response named '{name}' already exists.");
        }

        var before = Map(response);
        response.Name = name;
        response.Body = request.Body.Trim();
        response.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);

        var after = Map(response);
        await auditService.WriteAsync(
            actor, "Updated", "CannedResponse", response.Id.ToString(), before, after, cancellationToken);
        return new(CannedResponseOutcome.Success, after);
    }

    public async Task<CannedResponseOutcome> DeleteAsync(
        Guid id,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        if (IsEndUser(actor))
        {
            return CannedResponseOutcome.Forbidden;
        }

        var response = await dbContext.CannedResponses.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (response is null)
        {
            return CannedResponseOutcome.NotFound;
        }

        var before = Map(response);
        dbContext.CannedResponses.Remove(response);
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditService.WriteAsync(
            actor, "Deleted", "CannedResponse", id.ToString(), before, null, cancellationToken);
        return CannedResponseOutcome.Success;
    }

    public async Task<RenderResult> RenderAsync(
        Guid id,
        RenderCannedResponseRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        if (IsEndUser(actor))
        {
            return new(CannedResponseOutcome.Forbidden);
        }

        var response = await dbContext.CannedResponses.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (response is null)
        {
            return new(CannedResponseOutcome.NotFound);
        }

        var ticket = await dbContext.Tickets
            .SingleOrDefaultAsync(item => item.Id == request.TicketId, cancellationToken);
        if (ticket is null)
        {
            return new(CannedResponseOutcome.TicketNotFound);
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ticket.id"] = ticket.Id.ToString(),
            ["ticket.number"] = ticket.Number,
            ["ticket.title"] = ticket.Title,
            ["requester.name"] = ticket.RequesterDisplayName ?? ticket.RequesterId,
            ["agent.name"] = ActorDisplayName(actor),
        };
        return new(
            CannedResponseOutcome.Success,
            new RenderedCannedResponse(response.Id, response.Name, CannedResponseRenderer.Render(response.Body, values)));
    }

    private Task<bool> NameTakenAsync(string name, Guid? excludingId, CancellationToken cancellationToken) =>
        dbContext.CannedResponses.AnyAsync(
            response => response.Name.ToLower() == name.ToLower() && response.Id != excludingId,
            cancellationToken);

    private static bool IsEndUser(ClaimsPrincipal actor) => actor.IsInRole("EndUser");
    private static string ActorId(ClaimsPrincipal actor) =>
        actor.FindFirstValue("sub") ?? actor.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException("An authenticated actor identifier is required.");
    private static string ActorDisplayName(ClaimsPrincipal actor) =>
        actor.FindFirstValue("name") ?? actor.Identity?.Name ?? actor.FindFirstValue("preferred_username")
        ?? ActorId(actor);

    private static CannedResponseResponse Map(CannedResponse response) => new(
        response.Id, response.Name, response.Body, response.CreatedById, response.CreatedAt, response.UpdatedAt);
}
