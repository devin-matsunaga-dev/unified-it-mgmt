using System.Security.Claims;

using Microsoft.EntityFrameworkCore;

using Modules.Helpdesk.Data;
using Modules.Helpdesk.Features.Tickets;

using Platform.Search;

namespace Modules.Helpdesk.Features.Search;

/// <summary>
/// Tickets, from Helpdesk's own <c>helpdesk.tickets</c> (WP-5.4). Matched on the weighted tsvector the
/// table generates — title first, requester next, description last — or on the ticket number, which is what
/// somebody usually pastes.
/// </summary>
public sealed class TicketSearchSource(HelpdeskDbContext dbContext) : ISearchSource
{
    public SearchResultType Type => SearchResultType.Ticket;

    /// <summary>
    /// Everybody, which makes this the only source of the five an end user can reach — and the reason the
    /// endpoint is behind plain authentication rather than an operator policy. What an end user is allowed
    /// to <em>find</em> is narrowed inside the query below, never here: a yes-or-no gate cannot express
    /// "your own tickets and nobody else's".
    /// </summary>
    public bool IsVisibleTo(ClaimsPrincipal actor) => true;

    public async Task<SearchSourceResult> SearchAsync(
        SearchQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        // No Include: the status name is reached through the projection below, which joins it without
        // loading a ticket entity per row.
        var tickets = dbContext.Tickets.AsQueryable();

        // WP-5.4's own verification step, and the invariant behind it (ARCHITECTURE §6): an end user sees
        // only their own tickets, enforced in the query rather than in the UI. Applied before the search
        // predicate so the narrowing is part of what the index is asked, and applied to the count as well —
        // a total that included other people's tickets would leak how many there are even while hiding them.
        if (!SearchVisibility.IsAgent(query.Actor))
        {
            // A principal with no identity claim at all can own nothing, so it matches nothing. Falling
            // through to an unfiltered query here would be the whole leak.
            var actorId = SearchVisibility.ActorId(query.Actor);
            tickets = actorId is null
                ? tickets.Where(_ => false)
                : tickets.Where(ticket => ticket.RequesterId == actorId);
        }

        // "INC-000042", "INC 42" and "42" are all the same request. WP-1.10's rule, reused rather than
        // re-derived, so the search box and the ticket list resolve a pasted number identically.
        var sequenceNumber = TicketSearchQuery.ToSequenceNumber(query.Term);
        tickets = tickets.Where(ticket =>
            ticket.SearchVector.Matches(EF.Functions.ToTsQuery(SearchTerm.Configuration, query.TsQuery))
            || (sequenceNumber != null && ticket.SequenceNumber == sequenceNumber));

        var total = await tickets.CountAsync(cancellationToken);

        // Comments are deliberately not searched here, unlike the ticket list. A hit whose matching words
        // are three screens down an unopened ticket cannot be explained in a one-line dropdown row — and the
        // internal-note rule that goes with it (an end user must never learn a note exists) is a filter this
        // read would have to get exactly right to stay safe. The ticket list is where a comment search
        // belongs, and it already has one.
        var rows = await tickets
            .OrderByDescending(ticket =>
                ticket.SearchVector.Rank(EF.Functions.ToTsQuery(SearchTerm.Configuration, query.TsQuery)))
            .ThenByDescending(ticket => ticket.CreatedAt)
            .Take(query.Limit)
            .Select(ticket => new
            {
                ticket.Id,
                ticket.SequenceNumber,
                ticket.Title,
                ticket.RequesterDisplayName,
                StatusName = ticket.Status.Name,
            })
            .ToListAsync(cancellationToken);

        var hits = rows
            .Select(row => new SearchHit(
                SearchResultType.Ticket,
                row.Id,
                row.Title,
                // Formatted here rather than projected, because Ticket.Number is `builder.Ignore`d.
                $"INC-{row.SequenceNumber:000000}",
                row.RequesterDisplayName,
                row.StatusName))
            .ToList();

        return new SearchSourceResult(hits, total);
    }
}
