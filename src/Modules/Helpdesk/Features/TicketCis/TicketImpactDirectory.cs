using Microsoft.EntityFrameworkCore;

using Modules.Helpdesk.Data;
using Modules.Helpdesk.Features.Sla;

using Platform.Integration;

namespace Modules.Helpdesk.Features.TicketCis;

/// <summary>
/// Helpdesk's side of <see cref="ITicketImpactDirectory"/>: what is already open, and what is already on
/// the clock, across every CI an outage would take with it (WP-5.2).
/// <para>
/// One statement for the whole blast radius rather than a query per CI, for the reason Assets'
/// <c>CiDependencyDirectory</c> gives about correlation: the set is largest exactly when the estate is
/// worst, and a per-CI walk would make the biggest outage the most expensive thing the platform does.
/// </para>
/// </summary>
public sealed class TicketImpactDirectory(HelpdeskDbContext dbContext) : ITicketImpactDirectory
{
    /// <summary>The most tickets one call will return, whatever the caller asks for.</summary>
    internal const int MaximumLimit = 200;

    public async Task<ImpactedTicketSet> GetOpenTicketsForCisAsync(
        IReadOnlyCollection<Guid> ciIds,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ciIds);

        if (ciIds.Count == 0)
        {
            return new([], 0);
        }

        var ids = ciIds.Distinct().ToArray();
        var open = dbContext.TicketCiLinks
            .AsNoTracking()
            .Where(link => ids.Contains(link.CiId)
                && link.Ticket.StatusId != DefaultTicketStatuses.ResolvedId
                && link.Ticket.StatusId != DefaultTicketStatuses.ClosedId);

        // Counted over the same predicate rather than off the returned page, so a truncated panel can
        // still state the honest total (WP-2.4). Counted *distinct*, because one ticket linked to two CIs
        // inside the radius is one thing somebody is working on and two rows in this join — a total that
        // said two would report an outage as busier than it is.
        var total = await open.Select(link => link.TicketId).Distinct().CountAsync(cancellationToken);

        var rows = await open
            .Select(link => new { link.CiId, link.TicketId, link.Ticket, Status = link.Ticket.Status.Name })
            // Worst priority first, then oldest, so the ordering is decided before the cap is applied and
            // a truncated list is the top of the real one rather than an arbitrary slice. The final sort
            // is by SLA exposure, in memory, because a business-hours clock is not SQL.
            .OrderByDescending(row => row.Ticket.Priority)
            .ThenBy(row => row.Ticket.CreatedAt)
            .ThenBy(row => row.TicketId)
            .Take(Math.Clamp(limit, 1, MaximumLimit))
            .ToListAsync(cancellationToken);

        if (rows.Count == 0)
        {
            return new([], total);
        }

        // The clocks in a second query rather than a subquery in the projection above: the calculation
        // needs the policy and its calendar loaded, and an `Include` inside a projection is not something
        // EF can translate. A ticket whose priority matched no policy when it was raised has no row here
        // at all, and that is a real state — nothing is on the clock — rather than a failed read.
        var ticketIds = rows.Select(row => row.TicketId).Distinct().ToArray();
        var clocks = await dbContext.TicketSlas
            .AsNoTracking()
            .Include(sla => sla.Calendar)
            .Where(sla => ticketIds.Contains(sla.TicketId))
            .ToDictionaryAsync(sla => sla.TicketId, cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var tickets = rows
            // `Number` is a computed property, so it is read off the materialised entity rather than
            // rebuilt here — one place still formats a ticket number.
            .Select(row => new ImpactedTicketSummary(
                row.TicketId,
                row.CiId,
                row.Ticket.Number,
                row.Ticket.Title,
                row.Status,
                row.Ticket.Priority.ToString(),
                row.Ticket.CreatedAt,
                clocks.TryGetValue(row.TicketId, out var sla) ? SlaClock.Exposure(sla, now) : null))
            .OrderByDescending(ticket => ticket.Sla?.Breached ?? false)
            .ThenByDescending(ticket => ticket.Sla?.AtRisk ?? false)
            // A ticket with no SLA sorts last on this key rather than first: no deadline is not the most
            // urgent deadline.
            .ThenBy(ticket => ticket.Sla?.ResolutionDueAt ?? DateTimeOffset.MaxValue)
            .ThenBy(ticket => ticket.CreatedAt)
            .ThenBy(ticket => ticket.TicketId)
            .ToList();

        return new(tickets, total);
    }
}
