namespace Modules.Helpdesk.Data;

/// <summary>
/// The durable half of WP-3.6's alert→ticket automation: one row per problem, keyed by the dedupe key
/// <c>alert:{deviceId}:{ruleId}</c> that WP-3.5 promised would be identical across a restart and on
/// every recurrence.
/// <para>
/// It exists because "one ticket per alert" has to survive a Redis flush and a host restart
/// (ARCHITECTURE §5), and because the unique index on <see cref="DedupeKey"/> makes that a database
/// constraint rather than only a consumer invariant — the same argument WP-3.5 made for its filtered
/// unique index on open alerts.
/// </para>
/// <para>
/// <see cref="TicketId"/> is nullable on purpose: an alert the circuit breaker refused still gets a
/// row, so a storm is legible afterwards instead of being silently dropped.
/// </para>
/// </summary>
public sealed class AlertTicket
{
    public Guid Id { get; set; }

    /// <summary><c>alert:{deviceId}:{ruleId}</c>. Unique — this is the "one ticket per alert" rule.</summary>
    public string DedupeKey { get; set; } = string.Empty;

    public Guid DeviceId { get; set; }
    public Guid CiId { get; set; }
    public string RuleId { get; set; } = string.Empty;

    /// <summary>The most recent alert row this concerns. An escalation keeps the same id; a fresh raise after a clear does not.</summary>
    public Guid AlertId { get; set; }

    /// <summary>Null while no ticket exists — either the breaker was open or the rate limit refused it.</summary>
    public Guid? TicketId { get; set; }
    public Ticket? Ticket { get; set; }

    public string LastSeverity { get; set; } = string.Empty;

    /// <summary>How many times this problem has been raised, counting the first.</summary>
    public int OccurrenceCount { get; set; }

    /// <summary>How many raises produced no ticket because the automation was bounded.</summary>
    public int SuppressedCount { get; set; }

    /// <summary>How many tickets this rule has opened in total — a flapping rule that is resolved and re-raised opens more than one.</summary>
    public int TicketCount { get; set; }

    public DateTimeOffset FirstRaisedAt { get; set; }
    public DateTimeOffset LastRaisedAt { get; set; }
    public DateTimeOffset? LastClearedAt { get; set; }

    /// <summary>When the current ticket was opened. Indexed: the circuit breaker counts these when Redis cannot answer.</summary>
    public DateTimeOffset? TicketCreatedAt { get; set; }

    public DateTimeOffset? AutoResolvedAt { get; set; }
}
