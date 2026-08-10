namespace Modules.Helpdesk.Features.AlertTickets;

/// <summary>
/// The bounds ARCHITECTURE §7 requires of alert→ticket automation. Every one of them has a default,
/// because an unconfigured deployment must still be bounded rather than unbounded.
/// </summary>
public sealed class AlertTicketOptions
{
    public const string SectionName = "Helpdesk:AlertTickets";

    /// <summary>Off switch for the whole automation. Alerts still raise and clear; nothing is ticketed.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// The queue automated tickets enter, by name — the WP-1.8 portal precedent. A queue that does not
    /// exist falls back to the first one, then to no queue at all, rather than losing the ticket.
    /// </summary>
    public string QueueName { get; set; } = "Service Desk";

    /// <summary>
    /// Tickets one rule may open per minute. Mostly bites a rule that flaps across a resolve: the
    /// dedupe row already caps an <em>open</em> problem at one ticket, so this is what stops a rule
    /// whose ticket keeps being auto-resolved from opening a new one every cycle.
    /// </summary>
    public int RateLimitPerRulePerMinute { get; set; } = 3;

    /// <summary>
    /// Tickets the automation may open in <see cref="BreakerWindowSeconds"/> across every rule before
    /// it stops. Deliberately global: fifty distinct rules failing at once each pass their own
    /// per-rule limit, and that is exactly the storm this exists for.
    /// </summary>
    public int BreakerThreshold { get; set; } = 10;

    public int BreakerWindowSeconds { get; set; } = 60;

    /// <summary>How long the breaker stays open once tripped, before it lets one more window through.</summary>
    public int BreakerCooldownSeconds { get; set; } = 300;

    /// <summary>Where the "automation stopped" notice goes. One notice per trip, not per suppressed alert.</summary>
    public string AdminRecipient { get; set; } = "it-admin@it-platform.local";
}
