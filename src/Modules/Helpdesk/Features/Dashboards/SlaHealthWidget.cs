using System.Security.Claims;

using Microsoft.EntityFrameworkCore;

using Modules.Helpdesk.Data;
using Modules.Helpdesk.Features.Sla;

using Platform.Actors;
using Platform.Dashboards;
using Platform.Integration;

using Modules.Helpdesk.Features.Tickets;

namespace Modules.Helpdesk.Features.Dashboards;

/// <summary>
/// Where the open queue stands against its SLA targets (WP-5.5), and which tickets are closest to the edge.
/// <para>
/// The arithmetic is <see cref="SlaClock"/>'s and not this widget's — the same call WP-5.2 made for the
/// blast radius. Two copies of the pause accounting would eventually disagree about a paused ticket, and
/// with this widget on screen the disagreement would be visible beside the ticket's own SLA card.
/// </para>
/// </summary>
public sealed class SlaHealthWidget(HelpdeskDbContext dbContext) : IDashboardWidget
{
    public DashboardWidgetType Type => DashboardWidgetType.SlaHealth;

    public string Title => "SLA health";

    /// <summary>
    /// Operators only. An end user's own tickets have SLAs too, but this is a tally over everybody's work —
    /// there is no narrowing of it that would still answer the question the card asks.
    /// </summary>
    public bool IsVisibleTo(ClaimsPrincipal actor) => ActorRoles.IsAgent(actor);

    public async Task<DashboardWidgetData> LoadAsync(
        DashboardWidgetQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var now = DateTimeOffset.UtcNow;

        // Bounded by how much work is open rather than by the size of the history — the same property
        // WP-5.1 relied on for the estate-wide open-alert query. The clock cannot be evaluated in SQL (it
        // walks a business calendar), so the rows are read and measured here.
        //
        // Flat columns rather than `Include(sla => sla.Policy).ThenInclude(…)`, and that is not a
        // preference: an Include is **ignored** once a query projects a shape other than the entity it
        // started from, so the policy and its calendar would arrive null and the clock would throw on the
        // first row — a failure this dashboard would then quietly report as one card that could not load.
        // Only the fields the clock reads are fetched, so the ticket descriptions stay in the database.
        var open = await dbContext.TicketSlas.AsNoTracking()
            .Where(sla => sla.ResolutionCompletedAt == null
                && sla.Ticket.StatusId != DefaultTicketStatuses.ResolvedId
                && sla.Ticket.StatusId != DefaultTicketStatuses.ClosedId)
            .Select(sla => new Row(
                sla.Ticket.Id,
                sla.Ticket.SequenceNumber,
                sla.Ticket.Type,
                sla.Ticket.Title,
                sla.Ticket.Priority,
                sla.AccumulatedBusinessSeconds,
                sla.ActiveSince,
                sla.ResolutionCompletedAt,
                sla.Policy.Name,
                sla.Policy.ResolutionTargetMinutes,
                sla.Policy.WarningPercent,
                sla.Policy.Calendar.TimeZoneId,
                sla.Policy.Calendar.WorkingDays,
                sla.Policy.Calendar.StartTime,
                sla.Policy.Calendar.EndTime))
            .ToListAsync(cancellationToken);

        var measured = open.Select(row => Measure(row, now)).ToList();

        var breached = measured.Where(entry => entry.Exposure.Breached).ToList();
        var atRisk = measured.Where(entry => entry.Exposure.AtRisk).ToList();
        var onTrack = measured.Count - breached.Count - atRisk.Count;

        // Worst first: everything breached, longest overdue at the top, then whatever is closest to
        // breaching. `RemainingSeconds` is floored at zero, so the overrun is what separates two breaches —
        // ordering by it alone would put every breached ticket in an arbitrary order.
        var rows = measured
            .Where(entry => entry.Exposure.Breached || entry.Exposure.AtRisk)
            .OrderBy(entry => entry.Exposure.Breached ? 0 : 1)
            .ThenByDescending(entry => entry.OverrunSeconds)
            .ThenBy(entry => entry.Exposure.RemainingSeconds)
            .ThenBy(entry => entry.Row.TicketId)
            .Take(query.RowLimit)
            .Select(entry => new DashboardRow(
                entry.Row.Title,
                $"{TicketNumber.Format(entry.Row.Type, entry.Row.SequenceNumber)} · {entry.Row.Priority}",
                entry.Exposure.Breached
                    ? $"Breached by {Describe(entry.OverrunSeconds)}"
                    : $"Due in {Describe(entry.Exposure.RemainingSeconds)}",
                entry.Exposure.Breached ? DashboardTone.Critical : DashboardTone.Warning,
                new DashboardLink(DashboardLinkTarget.Ticket, RecordId: entry.Row.TicketId),
                // Only where it means something. A breached SLA's due instant is "immediately" by
                // construction (SlaClock walks zero remaining time forward), so printing it beside a
                // ticket that has been overdue for a week would be a date nobody could make sense of.
                entry.Exposure.Breached ? null : entry.Exposure.ResolutionDueAt))
            .ToList();

        return new DashboardWidgetData(
            measured.Count == 0
                ? "No open ticket is running against an SLA."
                : $"{measured.Count} open ticket{(measured.Count == 1 ? "" : "s")} against an SLA target",
            // The headline is what is wrong rather than what exists: a manager reading one number off this
            // card wants the count they have to do something about.
            breached.Count,
            "Breaching now",
            [
                new DashboardSegment("Breached", breached.Count, DashboardTone.Critical),
                new DashboardSegment("At risk", atRisk.Count, DashboardTone.Warning),
                new DashboardSegment("On track", onTrack, DashboardTone.Ok),
            ],
            rows,
            breached.Count + atRisk.Count,
            new DashboardLink(DashboardLinkTarget.TicketList),
            // Red only when something is actually breaching: a nought that is always red teaches a reader
            // to stop looking at the colour.
            breached.Count > 0 ? DashboardTone.Critical
                : atRisk.Count > 0 ? DashboardTone.Warning
                : DashboardTone.Ok);
    }

    /// <summary>
    /// One SLA measured at <paramref name="now"/>, with the overrun the exposure record has no room for.
    /// <para>
    /// The entities are rebuilt from the flat row here rather than fetched as a graph, so that
    /// <see cref="SlaClock"/> — which is the one copy of this arithmetic and takes the real types — can be
    /// called without a second query per ticket.
    /// </para>
    /// </summary>
    private static Measurement Measure(Row row, DateTimeOffset now)
    {
        var sla = new TicketSla
        {
            AccumulatedBusinessSeconds = row.AccumulatedBusinessSeconds,
            ActiveSince = row.ActiveSince,
            ResolutionCompletedAt = row.ResolutionCompletedAt,
            Policy = new SlaPolicy
            {
                Name = row.PolicyName,
                ResolutionTargetMinutes = row.ResolutionTargetMinutes,
                WarningPercent = row.WarningPercent,
                Calendar = new BusinessHoursCalendar
                {
                    TimeZoneId = row.TimeZoneId,
                    WorkingDays = row.WorkingDays,
                    StartTime = row.StartTime,
                    EndTime = row.EndTime,
                },
            },
        };

        var exposure = SlaClock.Exposure(sla, now);
        var overrun = Math.Max(0, SlaClock.ElapsedSeconds(sla, now) - (row.ResolutionTargetMinutes * 60d));
        return new Measurement(row, exposure, overrun);
    }

    /// <summary>
    /// A span in the coarsest unit that still says something, in <b>business</b> time — the clock the target
    /// is measured on, so "2h left" means two hours of the working day and not two hours of a Sunday.
    /// Composed here rather than in the browser for WP-5.3's reason: the sentence needs the source numbers,
    /// and re-deriving it from a due date would put the same knowledge in two languages.
    /// </summary>
    private static string Describe(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return span.TotalDays >= 1
            ? $"{(int)span.TotalDays}d"
            : span.TotalHours >= 1
                ? $"{(int)span.TotalHours}h"
                : $"{Math.Max(1, (int)span.TotalMinutes)}m";
    }

    /// <summary>One open SLA, flat, holding exactly what the clock and the row on screen need.</summary>
    private sealed record Row(
        Guid TicketId,
        long SequenceNumber,
        TicketType Type,
        string Title,
        TicketPriority Priority,
        double AccumulatedBusinessSeconds,
        DateTimeOffset? ActiveSince,
        DateTimeOffset? ResolutionCompletedAt,
        string PolicyName,
        int ResolutionTargetMinutes,
        int WarningPercent,
        string TimeZoneId,
        BusinessDays WorkingDays,
        TimeOnly StartTime,
        TimeOnly EndTime);

    private sealed record Measurement(Row Row, SlaExposure Exposure, double OverrunSeconds);
}
