using System.Globalization;

using Contracts.Events;

using Modules.Helpdesk.Data;

namespace Modules.Helpdesk.Features.AlertTickets;

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

    public static AlertTicketDraft Compose(AlertRaised alert)
    {
        ArgumentNullException.ThrowIfNull(alert);
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
            $"CI: {alert.CiId}",
            $"Alert: {alert.AlertId}",
            $"Rule: {alert.RuleId}",
            string.Empty,
            "Opened automatically by monitoring. It resolves itself when the alert clears.");
        return new AlertTicketDraft(title, Truncate(description, 10_000), urgency, impact);
    }

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
