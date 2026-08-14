using System.Globalization;

using Modules.Assets.Data;

namespace Modules.Assets.Features.Timeline;

/// <summary>
/// Interleaves four sources onto one axis. Pure — no database, no clock, no configuration — so the whole
/// of it is asserted against hand-written history, following <c>ImpactAnalyzer</c> and <c>DriftAnalyzer</c>.
/// <para>
/// The caps are applied by the sources, one each, rather than here. That is the decision this feature
/// turns on: a single cap across the merged stream would let a switch with four hundred alerts push every
/// ticket, every lifecycle move and every edit off the bottom of its own timeline — the CI whose history
/// is busiest would be the one whose history you cannot read. Per-source caps mean the noisiest source
/// truncates and the others stay whole, and each says so on its own row.
/// </para>
/// </summary>
public static class CiTimelineAssembler
{
    /// <summary>Every kind, which is what an unfiltered request asks for.</summary>
    public static readonly IReadOnlyList<CiTimelineEventKind> AllKinds =
        [.. Enum.GetValues<CiTimelineEventKind>()];

    public static CiTimelineResponse Assemble(CiTimelineSubject subject, int limit)
    {
        ArgumentNullException.ThrowIfNull(subject);

        var kinds = subject.Kinds.Count == 0
            ? AllKinds
            : [.. AllKinds.Where(subject.Kinds.Contains)];

        var entries = new List<CiTimelineEntryResponse>();
        if (kinds.Contains(CiTimelineEventKind.Alert))
        {
            entries.AddRange(subject.Alerts.Alerts.Select(ToEntry));
        }

        if (kinds.Contains(CiTimelineEventKind.Ticket))
        {
            entries.AddRange(subject.Tickets.Tickets.Select(ToEntry));
        }

        if (kinds.Contains(CiTimelineEventKind.Lifecycle))
        {
            entries.AddRange(subject.Lifecycle.Select(ToEntry));
        }

        if (kinds.Contains(CiTimelineEventKind.Config))
        {
            entries.AddRange(subject.Audit.Entries.Select(ToEntry));
        }

        // Newest first, which is the way a history is read on a page that opens at the top. The two
        // tiebreaks exist so two things recorded in the same transaction — a lifecycle move and the audit
        // row that went with it — come back in the same order on every read rather than in whatever order
        // four lists happened to be concatenated.
        var ordered = entries
            .OrderByDescending(entry => entry.OccurredAt)
            .ThenBy(entry => entry.Kind)
            .ThenBy(entry => entry.Id)
            .ToList();

        var sources = new List<CiTimelineSourceResponse>
        {
            Source(CiTimelineEventKind.Alert, kinds, subject.Alerts.Alerts.Count, subject.Alerts.Total),
            Source(CiTimelineEventKind.Ticket, kinds, subject.Tickets.Tickets.Count, subject.Tickets.Total),
            Source(CiTimelineEventKind.Lifecycle, kinds, subject.Lifecycle.Count, subject.LifecycleTotal),
            Source(CiTimelineEventKind.Config, kinds, subject.Audit.Entries.Count, subject.Audit.Total),
        };

        var summary = new CiTimelineSummaryResponse(
            ordered.Count,
            sources.Sum(source => source.Total),
            sources.Any(source => source.Truncated),
            ordered.Count == 0 ? null : ordered[^1].OccurredAt,
            ordered.Count == 0 ? null : ordered[0].OccurredAt);

        return new CiTimelineResponse(
            subject.CiId,
            subject.CiName,
            subject.From,
            subject.To,
            limit,
            kinds,
            summary,
            sources,
            ordered);
    }

    /// <summary>
    /// A source's row. A kind the filter excluded reports zero of everything and <c>Requested: false</c>,
    /// so a browser can say "not shown" rather than "none" — the difference between a filter and a fact.
    /// </summary>
    private static CiTimelineSourceResponse Source(
        CiTimelineEventKind kind,
        IReadOnlyList<CiTimelineEventKind> kinds,
        int returned,
        int total)
    {
        if (!kinds.Contains(kind))
        {
            return new(kind, Requested: false, 0, 0, Truncated: false);
        }

        // Never claims a total below what it is showing: a source that answered with a longer list than
        // its own count cannot make the summary contradict the rows underneath it.
        var honest = Math.Max(total, returned);
        return new(kind, Requested: true, returned, honest, Truncated: honest > returned);
    }

    private static CiTimelineEntryResponse ToEntry(Platform.Integration.CiAlertHistoryEntry alert) =>
        new(CiTimelineEventKind.Alert,
            alert.AlertId,
            // Raised, not cleared. One alert is one row on the axis: emitting the recovery as a second
            // entry would double every noisy device's history and put "cleared" above "raised" as though
            // they were two problems. The recovery is stated on this row instead.
            alert.RaisedAt,
            alert.Summary,
            DescribeAlert(alert),
            // Nobody chose an alert, so it has no actor. The acknowledgement is in the detail, where it
            // belongs — it is a thing somebody did afterwards, not the author of the event.
            Actor: null,
            alert.Severity,
            alert.Status,
            Priority: null,
            alert.AlertId,
            alert.DeviceId,
            TicketId: null,
            TicketNumber: null,
            LinkedAt: null);

    private static CiTimelineEntryResponse ToEntry(Platform.Integration.CiTicketHistoryEntry ticket) =>
        new(CiTimelineEventKind.Ticket,
            ticket.TicketId,
            ticket.CreatedAt,
            ticket.Title,
            ticket.Type == nameof(TicketKind.ServiceRequest) ? "Service request" : "Incident",
            ticket.RequesterName,
            Severity: null,
            ticket.Status,
            ticket.Priority,
            AlertId: null,
            DeviceId: null,
            ticket.TicketId,
            ticket.Number,
            // Pointed at only when the two are materially apart. A ticket triaged onto its asset within
            // the minute is the normal case and saying so on every row would be noise.
            ticket.LinkedAt - ticket.CreatedAt >= TimeSpan.FromMinutes(1) ? ticket.LinkedAt : null);

    private static CiTimelineEntryResponse ToEntry(CiLifecycleEvent lifecycle) =>
        new(CiTimelineEventKind.Lifecycle,
            lifecycle.Id,
            lifecycle.OccurredAt,
            DescribeLifecycle(lifecycle),
            lifecycle.Note,
            lifecycle.ActorId,
            Severity: null,
            Status: null,
            Priority: null,
            AlertId: null,
            DeviceId: null,
            TicketId: null,
            TicketNumber: null,
            LinkedAt: null);

    private static CiTimelineEntryResponse ToEntry(Platform.Auditing.AuditTrailEntry audit) =>
        new(CiTimelineEventKind.Config,
            audit.Id,
            audit.OccurredAt,
            DescribeAction(audit.Action),
            AuditDiff.Describe(AuditDiff.ChangedFields(audit.BeforeJson, audit.AfterJson)),
            audit.ActorId,
            Severity: null,
            Status: null,
            Priority: null,
            AlertId: null,
            DeviceId: null,
            TicketId: null,
            TicketNumber: null,
            LinkedAt: null);

    /// <summary>
    /// The address it was found at, how long it lasted, and — where it applies — why nobody was told.
    /// </summary>
    private static string DescribeAlert(Platform.Integration.CiAlertHistoryEntry alert)
    {
        var parts = new List<string> { alert.DeviceAddress };

        parts.Add(alert.ClearedAt is { } clearedAt
            ? $"recovered after {Humanise(clearedAt - alert.RaisedAt)}"
            : "still open");

        // WP-5.1's suppressions are shown rather than hidden. The alert happened; what was withheld was
        // the message about it, and a timeline that dropped it would answer "was this machine affected on
        // Tuesday" with a no.
        var suppression = alert.Suppression switch
        {
            nameof(AlertSuppressionName.Maintenance) => "suppressed by a maintenance window",
            nameof(AlertSuppressionName.Flapping) => "suppressed as flapping",
            nameof(AlertSuppressionName.RootCause) => "suppressed under its root cause",
            _ => null,
        };
        if (suppression is not null)
        {
            parts.Add(suppression);
        }

        if (alert.AcknowledgedAt is not null)
        {
            parts.Add($"acknowledged by {alert.AcknowledgedByName ?? "somebody"}");
        }

        return string.Join(" · ", parts);
    }

    /// <summary>
    /// One line of plain English per lifecycle row.
    /// <para>
    /// The state labels and the assignment sentences mirror <c>web/src/features/assets/lifecycle.ts</c> by
    /// hand, and that is a hazard worth naming: the browser still renders the older per-CI cards from its
    /// own copy, so the two have to agree. Nothing fails to compile if one of them is reworded — the same
    /// standing risk <c>InterfaceMetricNames</c> carries against the poller. <c>CiTimelineAssemblerTests</c>
    /// pins this side.
    /// </para>
    /// </summary>
    private static string DescribeLifecycle(CiLifecycleEvent lifecycle)
    {
        if (lifecycle.Action is { } action)
        {
            var place = string.Join(" · ", new[] { lifecycle.DepartmentName, lifecycle.SiteName }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
            return action switch
            {
                CiAssignmentAction.CheckOut =>
                    $"{lifecycle.ToOwnerName ?? "Someone"} took it out{(place.Length > 0 ? $" ({place})" : string.Empty)}",
                CiAssignmentAction.CheckIn =>
                    $"{lifecycle.FromOwnerName ?? "The previous owner"} returned it{(place.Length > 0 ? $" to {place}" : string.Empty)}",
                CiAssignmentAction.Transfer =>
                    $"{lifecycle.FromOwnerName ?? "Someone"} handed it to {lifecycle.ToOwnerName ?? "someone else"}",
                _ => place.Length > 0 ? $"Moved to {place}" : "Placement cleared",
            };
        }

        return $"{StateLabel(lifecycle.FromState)} → {StateLabel(lifecycle.ToState)}";
    }

    private static string StateLabel(CiLifecycleState? state) => state switch
    {
        CiLifecycleState.Ordered => "Ordered",
        CiLifecycleState.InStock => "In stock",
        CiLifecycleState.Deployed => "Deployed",
        CiLifecycleState.InRepair => "In repair",
        CiLifecycleState.Retired => "Retired",
        CiLifecycleState.Disposed => "Disposed",
        _ => "Unknown",
    };

    /// <summary>
    /// What an audited action did, said the way somebody reading a history would say it. An action this
    /// does not recognise prints as itself rather than as "Changed": the audit log is written by every
    /// module in the platform and a timeline must not rename what it does not understand.
    /// </summary>
    private static string DescribeAction(string action) => action switch
    {
        "Created" => "Registered in the CMDB",
        "Updated" => "Record updated",
        "Deleted" => "Removed from the CMDB",
        _ => action,
    };

    /// <summary>
    /// A duration in the roundest unit that still says something. Deliberately not a timestamp: instants
    /// travel as instants and are converted at the UI (CONVENTIONS), while "recovered after 4 minutes" is
    /// the same sentence in every time zone.
    /// </summary>
    private static string Humanise(TimeSpan span)
    {
        if (span < TimeSpan.FromMinutes(1))
        {
            return "under a minute";
        }

        if (span < TimeSpan.FromHours(1))
        {
            return Plural(span.TotalMinutes, "minute");
        }

        return span < TimeSpan.FromDays(2)
            ? Plural(span.TotalHours, "hour")
            : Plural(span.TotalDays, "day");
    }

    private static string Plural(double value, string unit)
    {
        var whole = (int)Math.Floor(value);
        return string.Create(
            CultureInfo.InvariantCulture, $"{whole} {unit}{(whole == 1 ? string.Empty : "s")}");
    }

    /// <summary>
    /// The suppression reasons, spelled the way <c>AlertSuppression</c> spells them. Named here rather
    /// than referenced because Assets holds no reference to Monitoring — the value arrives across a port
    /// as a string, and this is the one place in the module that has to know what those strings are.
    /// </summary>
    private enum AlertSuppressionName
    {
        None,
        Maintenance,
        Flapping,
        RootCause,
    }

    /// <summary>The ticket types, on the same terms — <c>TicketType</c> lives in Helpdesk.</summary>
    private enum TicketKind
    {
        Incident,
        ServiceRequest,
    }
}
