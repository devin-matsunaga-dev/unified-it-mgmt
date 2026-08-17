using System.Globalization;

using Contracts.Events;

using Modules.Helpdesk.Data;

using Platform.Integration;

namespace Modules.Helpdesk.Features.AlertTickets;

/// <summary>
/// What the CMDB says about the CI an alert names, at the moment the ticket is written (WP-3.7). It is
/// read live through the Assets and Helpdesk ports rather than carried on the event, because a CI is
/// not a person who can leave the directory — but the ticket <em>description</em> is a fixed record of
/// what monitoring saw, so the same facts are also written into it once and never rewritten. The live
/// view an agent reads is the linked-asset card, which re-reads on every request.
/// </summary>
public sealed record AlertCiContext(CiSummary? Ci, IReadOnlyList<LinkedTicketSummary> OpenTickets)
{
    public static readonly AlertCiContext Unknown = new(null, []);
}

/// <summary>
/// One CI that is failing because the alert being ticketed is failing (WP-5.1), as the root-cause
/// ticket names it.
/// </summary>
/// <param name="Name">
/// Read through <see cref="ICiDirectory"/> at composition time, or null for a CI the CMDB no longer
/// holds — in which case the id is printed, because "something else went down and I cannot tell you
/// what" is still worth saying.
/// </param>
public sealed record ImpactedCi(Guid CiId, string? Name, string? Type, string Summary);

/// <summary>What an alert becomes when it is written down as a ticket.</summary>
public sealed record AlertTicketDraft(
    string Title,
    string Description,
    TicketLevel Urgency,
    TicketLevel Impact);

/// <summary>
/// Everything about turning an alert into ticket text, and nothing about databases, brokers or
/// clocks — so the whole matrix is unit-testable, following <c>AlertRules</c> on the monitoring side.
/// </summary>
public static class AlertTicketPolicy
{
    /// <summary>
    /// The key ARCHITECTURE §4 names for this consumer. It is <em>derived</em> from facts WP-3.5
    /// guarantees are stable — a rule id comes from the check id, not from an allocation — so the same
    /// problem produces the same key after a restart and on every recurrence.
    /// </summary>
    public static string DedupeKey(Guid deviceId, string ruleId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ruleId);
        return $"alert:{deviceId}:{ruleId}";
    }

    /// <summary>
    /// Severity maps to urgency and impact rather than straight to a priority, because
    /// <see cref="Tickets.TicketPriorityMatrix"/> owns that calculation for every other ticket and an
    /// automated one must not be the exception that disagrees with the matrix.
    /// </summary>
    public static (TicketLevel Urgency, TicketLevel Impact) Levels(string severity) =>
        severity.Equals("Critical", StringComparison.OrdinalIgnoreCase)
            ? (TicketLevel.High, TicketLevel.High)
            : (TicketLevel.Medium, TicketLevel.Medium);

    /// <summary>
    /// How many affected CIs the description lists by name before it starts counting. Twenty rather
    /// than WP-3.7's five for related tickets, because there the list was context beside the point and
    /// here it <em>is</em> the point — but a core switch can take a hundred CIs with it, and a ticket
    /// whose description is a hundred lines of inventory is one nobody reads to the end.
    /// </summary>
    private const int ImpactedCiLimit = 20;

    public static AlertTicketDraft Compose(
        AlertRaised alert,
        AlertCiContext context,
        IReadOnlyList<ImpactedCi>? impacted = null)
    {
        ArgumentNullException.ThrowIfNull(alert);
        ArgumentNullException.ThrowIfNull(context);
        var (urgency, impact) = Levels(alert.Severity);
        var title = Truncate($"[{alert.Severity}] {Headline(alert)}", 200);
        var description = string.Join(Environment.NewLine,
            alert.Summary,
            string.Empty,
            $"Raised at: {Instant(alert.RaisedAt)}",
            $"Severity: {alert.Severity}",
            $"Check: {alert.CheckName} ({alert.CheckId})",
            $"Metric: {alert.MetricName}{Reading(alert.Value, alert.Threshold)}",
            $"Sustained for: {alert.ConsecutiveBreaches} consecutive cycles",
            $"Device: {alert.DeviceId}",
            $"Alert: {alert.AlertId}",
            $"Rule: {alert.RuleId}",
            string.Empty,
            CmdbBlock(alert.CiId, context),
            ImpactBlock(impacted),
            string.Empty,
            "Opened automatically by monitoring. It resolves itself when the alert clears.");
        return new AlertTicketDraft(title, Truncate(description, 10_000), urgency, impact);
    }

    /// <summary>
    /// The CIs this outage took with it (WP-5.1) — the half of "open ONE root-cause ticket listing
    /// affected CIs" that makes the one ticket enough. Each of these has an alert of its own that was
    /// deliberately not published, so this list is the only place they are written down for whoever
    /// picks the ticket up.
    /// <para>
    /// Empty for the overwhelming majority of alerts, which explain nothing but themselves, and it
    /// contributes no heading at all in that case — a ticket that said "Affected: none" would invite
    /// the reader to wonder what was meant to be there.
    /// </para>
    /// <para>
    /// Dated rather than live, like the CMDB block above it: these are the CIs that were failing when
    /// the ticket was opened. One of them recovering does not rewrite the description, and the alert
    /// board is where the current grouping is read.
    /// </para>
    /// </summary>
    public static string ImpactBlock(IReadOnlyList<ImpactedCi>? impacted)
    {
        if (impacted is null || impacted.Count == 0)
        {
            return string.Empty;
        }

        var lines = new List<string>(impacted.Count + 3)
        {
            string.Empty,
            impacted.Count == 1
                ? "Affected by this (1 CI, its own alert suppressed under this one)"
                : $"Affected by this ({impacted.Count} CIs, their own alerts suppressed under this one)",
        };

        lines.AddRange(impacted
            .Take(ImpactedCiLimit)
            .Select(ci => $"- {Describe(ci)}: {ci.Summary}"));

        if (impacted.Count > ImpactedCiLimit)
        {
            lines.Add($"- …and {impacted.Count - ImpactedCiLimit} more, listed on the alert board.");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string Describe(ImpactedCi ci) => ci switch
    {
        { Name: { Length: > 0 } name, Type: { Length: > 0 } type } => $"{name} ({type})",
        { Name: { Length: > 0 } name } => name,
        // Named by id rather than omitted: the CI has gone from the CMDB since the alert was raised,
        // and a shorter list would misreport the size of the outage.
        _ => $"CI {ci.CiId} (no longer in the CMDB)",
    };

    /// <summary>
    /// The CMDB context WP-3.7 asks the ticket to carry, written into the description so the ticket
    /// still says what the estate looked like when the alert fired. The linked-asset card beside it is
    /// the live view; this is the dated one.
    /// </summary>
    public static string CmdbBlock(Guid ciId, AlertCiContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Ci is not { } ci)
        {
            // The CI is gone, or was never in the CMDB. Said plainly rather than left blank: an
            // "Owner: —" reads as an unassigned asset, which is a different fact.
            return string.Join(Environment.NewLine,
                "Asset context",
                $"CI: {ciId} — not found in the CMDB, so no owner, location or warranty could be read.");
        }

        var lines = new List<string>(9)
        {
            "Asset context",
            $"CI: {ci.Name} ({ci.Type}, {ciId})",
            $"Owner: {Or(ci.OwnerName, "nobody holds this asset")}",
            $"Location: {Or(ci.SiteName, "no site recorded")}",
            $"Department: {Or(ci.DepartmentName, "none recorded")}",
            $"Lifecycle: {ci.LifecycleState}",
            $"Warranty: {Warranty(ci)}",
        };
        if (ci.AssetTag is { Length: > 0 } tag)
        {
            lines.Insert(2, $"Asset tag: {tag}");
        }

        if (ci.ContractName is { Length: > 0 } contract)
        {
            lines.Add($"Support contract: {contract}");
        }

        lines.Add(context.OpenTickets.Count == 0
            ? "Open related tickets: none"
            : $"Open related tickets: {string.Join(", ", context.OpenTickets.Select(ticket => $"{ticket.Number} ({ticket.Status}, {ticket.Priority})"))}");
        return string.Join(Environment.NewLine, lines);
    }

    private static string Warranty(CiSummary ci) => (ci.WarrantyStatus, ci.WarrantyExpiresAt, ci.WarrantyDaysRemaining) switch
    {
        (null, _, _) or (_, null, _) => "no warranty date recorded",
        (_, { } expiry, { } days) when days < 0 =>
            $"expired {-days} day(s) ago on {Date(expiry)}",
        (_, { } expiry, { } days) => $"{ci.WarrantyStatus} — {days} day(s) left, expires {Date(expiry)}",
        (_, { } expiry, null) => $"{ci.WarrantyStatus} — expires {Date(expiry)}",
    };

    private static string Or(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value;

    private static string Date(DateOnly value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>
    /// What gets added to a ticket that already exists. Internal, because it is a note between the
    /// platform and the technician holding it rather than something to mail a requester per cycle.
    /// </summary>
    public static string RecurrenceNote(AlertRaised alert, int occurrenceCount, string? previousSeverity)
    {
        ArgumentNullException.ThrowIfNull(alert);
        var lead = previousSeverity is not null
                   && !previousSeverity.Equals(alert.Severity, StringComparison.OrdinalIgnoreCase)
            ? $"Alert escalated from {previousSeverity} to {alert.Severity}."
            : $"Alert raised again at {alert.Severity}.";
        return string.Join(Environment.NewLine,
            lead,
            $"Occurrence {occurrenceCount} of this rule; no second ticket was opened.",
            $"Observed at: {Instant(alert.RaisedAt)}",
            $"Metric: {alert.MetricName}{Reading(alert.Value, alert.Threshold)}",
            $"Rule: {alert.RuleId}");
    }

    /// <summary>
    /// The note the ticket is resolved with. "Resolved" requires one (WP-1.2 seeds the status that
    /// way), and a ticket that closes itself with no explanation is worse than one left open.
    /// </summary>
    public static string ResolutionNote(AlertCleared alert)
    {
        ArgumentNullException.ThrowIfNull(alert);
        return string.Join(Environment.NewLine,
            $"Monitoring cleared this alert automatically after {Duration(alert.DurationSeconds)}.",
            alert.Summary,
            $"Cleared at: {Instant(alert.OccurredAt)}",
            $"Last severity: {alert.PreviousSeverity}",
            $"Metric: {alert.MetricName}{Reading(alert.Value, null)}",
            $"Rule: {alert.RuleId}");
    }

    /// <summary>
    /// Left on the previous ticket when a rule recurs after that ticket has been finished. The WP-1.2
    /// status graph is linear and has no edge back out of Resolved or Closed, so the recurrence gets
    /// its own ticket and the two are joined by a note in each direction rather than silently.
    /// </summary>
    public static string SupersededNote(string newTicketNumber, string severity) =>
        $"This alert has recurred at {severity} and was recorded on {newTicketNumber}, because a resolved ticket cannot be reopened.";

    public static string SupersedesNote(string previousTicketNumber) =>
        $"The previous ticket for this alert, {previousTicketNumber}, was already finished; this recurrence was recorded here.";

    /// <summary>
    /// What an auto-remediation run leaves on the ticket (WP-5.6). Internal, like every other note this
    /// automation writes: it is between the platform and whoever is holding the ticket, and a requester
    /// mailed the stdout of a service restart learns nothing from it.
    /// <para>
    /// The output is quoted verbatim and last. It has already been truncated by the server that stored
    /// it, and rewriting an agent's output — trimming it, summarising it, reformatting it — is how the
    /// one line that explains a failure gets lost.
    /// </para>
    /// </summary>
    public static string RunbookNote(RunbookExecutionCompleted execution)
    {
        ArgumentNullException.ThrowIfNull(execution);
        var succeeded = execution.Outcome.Equals("Succeeded", StringComparison.OrdinalIgnoreCase);
        var lines = new List<string>(10)
        {
            succeeded
                ? $"Automated remediation '{execution.RunbookName}' ran successfully."
                : $"Automated remediation '{execution.RunbookName}' did not succeed ({execution.Outcome}).",
            string.Empty,
            $"Runbook: {execution.RunbookKey} (version {execution.RunbookVersion})",
            $"Requested by: {execution.RequestedBy}",
            $"Ran on: {execution.PollerName ?? "no poller claimed it"}",
            $"Finished: {Instant(execution.CompletedAt)} after {Duration(execution.DurationSeconds)}",
        };

        if (execution.ExitCode is { } exitCode)
        {
            lines.Add($"Exit code: {exitCode}");
        }

        if (!succeeded)
        {
            // Said explicitly, because "it failed" and "it will try again shortly" are the two things a
            // technician has to tell apart before deciding whether to touch anything.
            lines.Add(string.Empty);
            lines.Add("Nothing was retried and nothing will be: this ticket is the escalation.");
        }

        if (!string.IsNullOrWhiteSpace(execution.Error))
        {
            lines.Add(string.Empty);
            lines.Add("Error");
            lines.Add(execution.Error.TrimEnd());
        }

        if (!string.IsNullOrWhiteSpace(execution.Output))
        {
            lines.Add(string.Empty);
            lines.Add("Output");
            lines.Add(execution.Output.TrimEnd());
        }

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// The ticket a failed remediation opens when the alert never had one — the automation was off,
    /// suppressed, or the alert cleared before it was ticketed.
    /// <para>
    /// Opened only on failure. A remediation that worked and had no ticket needs none: it is recorded
    /// on the execution row and in the audit trail, and a ticket saying "something was fixed
    /// automatically, no action required" is a ticket somebody has to close.
    /// </para>
    /// </summary>
    public static AlertTicketDraft ComposeRunbookEscalation(RunbookExecutionCompleted execution)
    {
        ArgumentNullException.ThrowIfNull(execution);
        var title = Truncate(
            $"[Remediation failed] {execution.RunbookName} on device {execution.DeviceId}", 200);
        var description = string.Join(Environment.NewLine,
            $"An automated remediation failed and there was no open ticket to record it on.",
            string.Empty,
            RunbookNote(execution),
            string.Empty,
            $"Device: {execution.DeviceId}",
            $"CI: {execution.CiId}",
            execution.AlertId is { } alertId ? $"Alert: {alertId}" : "Alert: none — this was run by hand",
            execution.RuleId is { } ruleId ? $"Rule: {ruleId}" : "Rule: none",
            string.Empty,
            "Opened automatically because the remediation escalated. It does not resolve itself.");
        // High on both, whatever the alert's severity was. Something ran on a machine and failed, and
        // nobody is watching it — that is the definition of the case this ticket exists for.
        return new AlertTicketDraft(title, Truncate(description, 10_000), TicketLevel.High, TicketLevel.High);
    }

    /// <summary>Left on the alert row's ticket when the automation refused to open one.</summary>
    public static string SuppressedNote(AlertRaised alert, string reason) =>
        $"A raise of {alert.RuleId} at {Instant(alert.RaisedAt)} opened no ticket: {reason}.";

    /// <summary>
    /// The check name in front of the summary — unless the summary already opens with it, which
    /// WP-3.5's summaries do ("SNMP: CPU: cpu.utilisation_percent is 91%…", "Reachability on
    /// 192.0.2.1 is failing…"). Prefixing unconditionally produced
    /// <c>[Critical] SNMP: CPU: SNMP: CPU: …</c> on the live estate, which is a ticket title an
    /// operator has to read twice to parse. Found by hand-verification (2026-08-11).
    /// </summary>
    private static string Headline(AlertRaised alert) =>
        alert.Summary.StartsWith(alert.CheckName, StringComparison.OrdinalIgnoreCase)
            ? alert.Summary
            : $"{alert.CheckName}: {alert.Summary}";

    private static string Reading(double? value, double? threshold)
    {
        if (value is null && threshold is null)
        {
            return string.Empty;
        }

        var parts = new List<string>(2);
        if (value is { } reading)
        {
            parts.Add($"value {reading.ToString("0.###", CultureInfo.InvariantCulture)}");
        }

        if (threshold is { } limit)
        {
            parts.Add($"threshold {limit.ToString("0.###", CultureInfo.InvariantCulture)}");
        }

        return $" ({string.Join(", ", parts)})";
    }

    private static string Duration(long seconds) => seconds switch
    {
        < 60 => $"{seconds}s",
        < 3600 => $"{seconds / 60}m {seconds % 60}s",
        _ => $"{seconds / 3600}h {seconds % 3600 / 60}m",
    };

    private static string Instant(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);

    private static string Truncate(string value, int maximum) =>
        value.Length <= maximum ? value : value[..(maximum - 1)] + "…";
}
