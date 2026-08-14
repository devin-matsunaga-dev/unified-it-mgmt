using Microsoft.EntityFrameworkCore;

using Modules.Helpdesk.Data;

using Platform.Integration;

namespace Modules.Helpdesk.Features.TicketCis;

/// <summary>
/// Helpdesk's side of <see cref="ICiTicketHistoryDirectory"/>: every ticket ever raised about one CI, for
/// the timeline on its asset page (WP-5.3).
/// <para>
/// Every status, unlike the two ports beside it. A delete guard and a blast radius both ask about work
/// that is still to be done; a timeline is mostly work that is finished, and the resolved ticket from
/// three months ago is the row somebody scrolls back to find when the same fault returns.
/// </para>
/// </summary>
public sealed class CiTicketHistoryDirectory(HelpdeskDbContext dbContext) : ICiTicketHistoryDirectory
{
    /// <summary>The most tickets one call will return, whatever the caller asks for.</summary>
    internal const int MaximumLimit = 200;

    public async Task<CiTicketHistory> GetTicketsForCiAsync(
        Guid ciId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        int limit,
        CancellationToken cancellationToken)
    {
        // Windowed on the ticket's own CreatedAt rather than on when it was linked here. A ticket takes
        // the moment it was raised on the axis — that is when the thing it describes happened — and a
        // timeline windowed on the link time would move a March incident into May because somebody
        // tidied the CMDB. The link time travels alongside so the entry can say the two differ.
        var matching = dbContext.TicketCiLinks
            .AsNoTracking()
            .Where(link => link.CiId == ciId);

        if (from is not null)
        {
            matching = matching.Where(link => link.Ticket.CreatedAt >= from);
        }

        if (to is not null)
        {
            matching = matching.Where(link => link.Ticket.CreatedAt <= to);
        }

        var total = await matching.CountAsync(cancellationToken);

        var rows = await matching
            .OrderByDescending(link => link.Ticket.CreatedAt)
            .ThenByDescending(link => link.TicketId)
            .Take(Math.Clamp(limit, 1, MaximumLimit))
            .Select(link => new
            {
                link.TicketId,
                link.Ticket,
                Status = link.Ticket.Status.Name,
                link.LinkedAt,
            })
            .ToListAsync(cancellationToken);

        return new(
            [
                // `Number` is a computed property, so it is read off the materialised entity rather than
                // rebuilt here — one place still formats a ticket number.
                .. rows.Select(row => new CiTicketHistoryEntry(
                    row.TicketId,
                    row.Ticket.Number,
                    row.Ticket.Title,
                    row.Status,
                    row.Ticket.Priority.ToString(),
                    row.Ticket.Type.ToString(),
                    row.Ticket.RequesterDisplayName,
                    row.Ticket.CreatedAt,
                    row.LinkedAt)),
            ],
            total);
    }
}
