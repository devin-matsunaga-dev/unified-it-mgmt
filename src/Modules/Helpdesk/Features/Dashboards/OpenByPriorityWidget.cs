using System.Security.Claims;

using Microsoft.EntityFrameworkCore;

using Modules.Helpdesk.Data;

using Platform.Actors;
using Platform.Dashboards;

namespace Modules.Helpdesk.Features.Dashboards;

/// <summary>
/// What is open, split by priority (WP-5.5), with the newest critical work named underneath.
/// <para>
/// Counted in the database rather than measured in memory, unlike <see cref="SlaHealthWidget"/>: nothing
/// here needs a business calendar, so it is one grouped count over an indexed column.
/// </para>
/// </summary>
public sealed class OpenByPriorityWidget(HelpdeskDbContext dbContext) : IDashboardWidget
{
    public DashboardWidgetType Type => DashboardWidgetType.OpenByPriority;

    public string Title => "Open tickets by priority";

    public bool IsVisibleTo(ClaimsPrincipal actor) => ActorRoles.IsAgent(actor);

    public async Task<DashboardWidgetData> LoadAsync(
        DashboardWidgetQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        // "Open" is everything that is not Resolved or Closed — the same predicate the SLA pass, the
        // ticket-CI directory and the blast radius all use. Spelt out rather than hidden behind a helper
        // because EF has to translate it.
        var open = dbContext.Tickets.AsNoTracking()
            .Where(ticket => ticket.StatusId != DefaultTicketStatuses.ResolvedId
                && ticket.StatusId != DefaultTicketStatuses.ClosedId);

        var counts = await open
            .GroupBy(ticket => ticket.Priority)
            .Select(group => new { Priority = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);
        var byPriority = counts.ToDictionary(entry => entry.Priority, entry => entry.Count);

        // Every priority is drawn, including the ones nobody has any of. A band that vanishes when it
        // reaches zero makes the card change shape week to week and hides the good news that there is
        // nothing critical open.
        var segments = new[]
        {
            Segment(TicketPriority.Critical, DashboardTone.Critical),
            Segment(TicketPriority.High, DashboardTone.Warning),
            Segment(TicketPriority.Medium, DashboardTone.Info),
            Segment(TicketPriority.Low, DashboardTone.Neutral),
        };

        var total = counts.Sum(entry => entry.Count);
        var urgent = await open
            .Where(ticket => ticket.Priority == TicketPriority.Critical || ticket.Priority == TicketPriority.High)
            .OrderByDescending(ticket => ticket.Priority)
            .ThenByDescending(ticket => ticket.CreatedAt)
            .Take(query.RowLimit)
            .Select(ticket => new
            {
                ticket.Id,
                ticket.SequenceNumber,
                ticket.Title,
                ticket.Priority,
                StatusName = ticket.Status.Name,
                ticket.CreatedAt,
            })
            .ToListAsync(cancellationToken);

        var rows = urgent
            .Select(ticket => new DashboardRow(
                ticket.Title,
                // Formatted here rather than projected, because Ticket.Number is `builder.Ignore`d.
                $"INC-{ticket.SequenceNumber:000000} · {ticket.StatusName}",
                ticket.Priority.ToString(),
                ticket.Priority == TicketPriority.Critical ? DashboardTone.Critical : DashboardTone.Warning,
                new DashboardLink(DashboardLinkTarget.Ticket, RecordId: ticket.Id),
                ticket.CreatedAt))
            .ToList();

        return new DashboardWidgetData(
            total == 0 ? "Nothing is open." : "Everything not resolved or closed",
            total,
            "Open",
            segments,
            rows,
            byPriority.GetValueOrDefault(TicketPriority.Critical)
                + byPriority.GetValueOrDefault(TicketPriority.High),
            new DashboardLink(DashboardLinkTarget.TicketList),
            // The count of open work is neither good nor bad news — a service desk with nothing open is a
            // service desk nobody is calling — so it is drawn in the reading colour and not in a warning one.
            DashboardTone.Neutral);

        DashboardSegment Segment(TicketPriority priority, DashboardTone tone) => new(
            priority.ToString(),
            byPriority.GetValueOrDefault(priority),
            tone,
            // The WP's own verification step: every widget deep-links to its filtered list. The value
            // travels in the domain's spelling and the browser turns it into a query parameter.
            new DashboardLink(DashboardLinkTarget.TicketList, priority.ToString()));
    }
}
