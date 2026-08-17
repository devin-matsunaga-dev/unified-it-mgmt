namespace Contracts.Events;

/// <summary>
/// An auto-remediation runbook has finished — successfully, unsuccessfully, or by running out of time.
/// <para>
/// Published once per execution and never per attempt, because there are no attempts: a runbook that
/// fails is not retried (WP-5.6 bounds automation the way ARCHITECTURE §7 invariant 4 requires), so
/// this event is the single, final fact about one execution.
/// </para>
/// <para>
/// It exists so that the result can reach a ticket without Monitoring writing one. Monitoring knows
/// what ran and what it printed; Helpdesk owns tickets and already holds the alert→ticket dedupe row
/// keyed on <c>alert:{DeviceId}:{RuleId}</c>, so it can find the ticket this belongs on itself. The
/// alternative — a port from Monitoring into Helpdesk — would be a write path, and ARCHITECTURE §3
/// says a port is a narrow read surface and never a substitute for an event.
/// </para>
/// </summary>
/// <param name="AlertId">
/// The alert whose trigger rule started this, or null for an execution an operator asked for by hand.
/// A null here is what tells Helpdesk not to go looking for a ticket: a manual run was watched by the
/// person who started it, and its record is the execution row and the audit entry.
/// </param>
/// <param name="RuleId">
/// The alert's rule, carried so the ticket can be found by the dedupe key Helpdesk already stores.
/// Null exactly when <paramref name="AlertId"/> is.
/// </param>
/// <param name="Outcome"><c>Succeeded</c>, <c>Failed</c> or <c>TimedOut</c>. Never a pending state — this is a completion.</param>
/// <param name="Output">
/// What the runbook printed, already truncated by the server. It is the operator-facing half of the
/// result and is written onto the ticket verbatim, so it must be bounded before it travels.
/// </param>
public sealed record RunbookExecutionCompleted(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid ExecutionId,
    Guid RunbookId,
    string RunbookKey,
    string RunbookName,
    int RunbookVersion,
    Guid? AlertId,
    Guid DeviceId,
    Guid CiId,
    string? RuleId,
    string Outcome,
    int? ExitCode,
    string? Output,
    string? Error,
    string RequestedBy,
    string? PollerName,
    DateTimeOffset RequestedAt,
    DateTimeOffset CompletedAt,
    long DurationSeconds);
