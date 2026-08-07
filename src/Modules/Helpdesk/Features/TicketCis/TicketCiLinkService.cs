using System.Security.Claims;

using Contracts.Events;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Modules.Helpdesk.Data;
using Platform.Auditing;
using Platform.Integration;

namespace Modules.Helpdesk.Features.TicketCis;

public sealed class TicketCiLinkService(
    HelpdeskDbContext dbContext,
    ICiDirectory ciDirectory,
    IPublishEndpoint publishEndpoint,
    IAuditService auditService) : ITicketCiLinkService
{
    public async Task<TicketCiLinkListResult> ListAsync(
        Guid ticketId,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        // The CMDB is an agent-only surface (WP-2.1). `CanManageTickets` deliberately includes EndUser
        // for the portal, so the boundary is enforced here rather than by the policy.
        if (IsEndUser(actor))
        {
            return new(TicketCiLinkOutcome.Forbidden);
        }

        if (!await dbContext.Tickets.AnyAsync(ticket => ticket.Id == ticketId, cancellationToken))
        {
            return new(TicketCiLinkOutcome.TicketNotFound);
        }

        return new(TicketCiLinkOutcome.Success, await MapAsync(ticketId, cancellationToken));
    }

    public async Task<TicketCiLinkResult> LinkAsync(
        Guid ticketId,
        LinkTicketCiRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        if (IsEndUser(actor))
        {
            return new(TicketCiLinkOutcome.Forbidden);
        }

        var ticket = await dbContext.Tickets.SingleOrDefaultAsync(
            item => item.Id == ticketId, cancellationToken);
        if (ticket is null)
        {
            return new(TicketCiLinkOutcome.TicketNotFound);
        }

        var summary = (await ciDirectory.GetSummariesAsync([request.CiId], cancellationToken)).SingleOrDefault();
        if (summary is null)
        {
            return new(TicketCiLinkOutcome.CiNotFound, Error: $"CI '{request.CiId}' does not exist.");
        }

        if (await dbContext.TicketCiLinks.AnyAsync(
                link => link.TicketId == ticketId && link.CiId == request.CiId, cancellationToken))
        {
            return new(
                TicketCiLinkOutcome.Duplicate,
                Error: $"'{summary.Name}' is already linked to {ticket.Number}.");
        }

        var now = DateTimeOffset.UtcNow;
        var actorId = GetActorId(actor);
        var link = new TicketCiLink
        {
            Id = Guid.CreateVersion7(),
            TicketId = ticketId,
            Ticket = ticket,
            CiId = request.CiId,
            LinkedById = actorId,
            LinkedByName = GetActorDisplayName(actor),
            LinkedAt = now,
        };

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        dbContext.TicketCiLinks.Add(link);
        await dbContext.SaveChangesAsync(cancellationToken);
        await publishEndpoint.Publish(
            new TicketCiLinked(Guid.CreateVersion7(), now, ticketId, ticket.Number, request.CiId, actorId),
            cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var response = Map(link, summary);
        await auditService.WriteAsync(
            actor, "Created", "TicketCiLink", link.Id.ToString(), null, response, cancellationToken);
        return new(TicketCiLinkOutcome.Success, response);
    }

    public async Task<TicketCiLinkOutcome> UnlinkAsync(
        Guid ticketId,
        Guid ciId,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        if (IsEndUser(actor))
        {
            return TicketCiLinkOutcome.Forbidden;
        }

        var ticket = await dbContext.Tickets.SingleOrDefaultAsync(item => item.Id == ticketId, cancellationToken);
        if (ticket is null)
        {
            return TicketCiLinkOutcome.TicketNotFound;
        }

        var link = await dbContext.TicketCiLinks.SingleOrDefaultAsync(
            item => item.TicketId == ticketId && item.CiId == ciId, cancellationToken);
        if (link is null)
        {
            return TicketCiLinkOutcome.LinkNotFound;
        }

        var summary = (await ciDirectory.GetSummariesAsync([ciId], cancellationToken)).SingleOrDefault();
        var before = Map(link, summary);
        var now = DateTimeOffset.UtcNow;
        var actorId = GetActorId(actor);

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        dbContext.TicketCiLinks.Remove(link);
        await dbContext.SaveChangesAsync(cancellationToken);
        await publishEndpoint.Publish(
            new TicketCiUnlinked(Guid.CreateVersion7(), now, ticketId, ticket.Number, ciId, actorId),
            cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        await auditService.WriteAsync(
            actor, "Deleted", "TicketCiLink", link.Id.ToString(), before, null, cancellationToken);
        return TicketCiLinkOutcome.Success;
    }

    private async Task<IReadOnlyList<TicketCiLinkResponse>> MapAsync(
        Guid ticketId,
        CancellationToken cancellationToken)
    {
        var links = await dbContext.TicketCiLinks
            .Where(link => link.TicketId == ticketId)
            .OrderBy(link => link.LinkedAt).ThenBy(link => link.Id)
            .ToListAsync(cancellationToken);
        if (links.Count == 0)
        {
            return [];
        }

        var summaries = (await ciDirectory.GetSummariesAsync(
                [.. links.Select(link => link.CiId)], cancellationToken))
            .ToDictionary(summary => summary.Id);
        return [.. links.Select(link => Map(link, summaries.GetValueOrDefault(link.CiId)))];
    }

    /// <summary>
    /// A missing summary means the CI was deleted out from under the link, which the Assets delete
    /// guard refuses; the row still renders rather than disappearing from the ticket without trace.
    /// </summary>
    private static TicketCiLinkResponse Map(TicketCiLink link, CiSummary? summary) => new(
        link.Id,
        link.TicketId,
        link.CiId,
        summary?.Name ?? "Unavailable CI",
        summary?.Type ?? "Unknown",
        summary?.AssetTag,
        summary?.SerialNumber,
        summary?.LifecycleState ?? "Unknown",
        summary?.IsActive ?? false,
        summary?.OwnerName,
        summary?.SiteName,
        link.LinkedById,
        link.LinkedByName,
        link.LinkedAt);

    private static bool IsEndUser(ClaimsPrincipal actor) => actor.IsInRole("EndUser");

    private static string GetActorId(ClaimsPrincipal actor) =>
        actor.FindFirstValue("sub") ?? actor.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException("An authenticated actor identifier is required.");

    private static string GetActorDisplayName(ClaimsPrincipal actor) =>
        actor.FindFirstValue("name") ?? actor.Identity?.Name ?? actor.FindFirstValue("preferred_username")
        ?? GetActorId(actor);
}

/// <summary>
/// Helpdesk's implementation of the ticket-link port: the one thing Assets needs to know before it
/// deletes a CI.
/// </summary>
public sealed class TicketCiLinkDirectory(HelpdeskDbContext dbContext) : ITicketLinkDirectory
{
    public Task<int> CountLinksForCiAsync(Guid ciId, CancellationToken cancellationToken) =>
        dbContext.TicketCiLinks.CountAsync(link => link.CiId == ciId, cancellationToken);
}
