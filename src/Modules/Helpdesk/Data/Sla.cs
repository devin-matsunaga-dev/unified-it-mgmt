namespace Modules.Helpdesk.Data;

[Flags]
public enum BusinessDays
{
    None = 0,
    Monday = 1,
    Tuesday = 2,
    Wednesday = 4,
    Thursday = 8,
    Friday = 16,
    Saturday = 32,
    Sunday = 64,
    Weekdays = Monday | Tuesday | Wednesday | Thursday | Friday,
}

public sealed class BusinessHoursCalendar
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string TimeZoneId { get; set; } = "UTC";
    public BusinessDays WorkingDays { get; set; } = BusinessDays.Weekdays;
    public TimeOnly StartTime { get; set; }
    public TimeOnly EndTime { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// One rule in an ordered list: the conditions a ticket must meet, and the targets it then gets.
///
/// <para>
/// Every condition is optional and null means "any", so a policy with none is the catch-all. The
/// first active policy in <see cref="SortOrder"/> whose conditions all match is the one that
/// applies — the model Zendesk and Jira Service Management use, and the reason order is a column an
/// administrator sets rather than an accident of creation time.
/// </para>
/// </summary>
public sealed class SlaPolicy
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>Lower is evaluated first. Ties fall back to <see cref="CreatedAt"/> for determinism.</summary>
    public int SortOrder { get; set; }

    public TicketPriority? Priority { get; set; }

    /// <summary>Incidents and service requests usually carry different targets; null matches both.</summary>
    public TicketType? TicketType { get; set; }

    /// <summary>
    /// Matched by id rather than by the old free-text name, so renaming a category in Settings
    /// cannot silently detach the SLA that was written for it.
    /// </summary>
    public Guid? CategoryId { get; set; }
    public TicketCategory? Category { get; set; }

    public int ResponseTargetMinutes { get; set; }
    public int ResolutionTargetMinutes { get; set; }
    public int WarningPercent { get; set; } = 80;
    public Guid CalendarId { get; set; }
    public BusinessHoursCalendar Calendar { get; set; } = null!;
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// One ticket's clock.
///
/// <para>
/// The targets are <b>copied here</b> when the policy attaches rather than read through it. Editing
/// a policy would otherwise re-target every ticket already running against it — tighten a target at
/// lunchtime and work that was on track is retrospectively breached, with notifications firing and
/// no record that anything moved. Every real SLA engine snapshots for this reason, and
/// <see cref="PolicyId"/> is kept only to say which rule was applied.
/// </para>
/// </summary>
public sealed class TicketSla
{
    public Guid Id { get; set; }
    public Guid TicketId { get; set; }
    public Ticket Ticket { get; set; } = null!;

    /// <summary>Which policy attached. Provenance only — nothing is measured through it.</summary>
    public Guid PolicyId { get; set; }
    public SlaPolicy Policy { get; set; } = null!;

    public int ResponseTargetMinutes { get; set; }
    public int ResolutionTargetMinutes { get; set; }
    public int WarningPercent { get; set; }
    public Guid CalendarId { get; set; }
    public BusinessHoursCalendar Calendar { get; set; } = null!;

    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? ActiveSince { get; set; }
    public double AccumulatedBusinessSeconds { get; set; }
    public double? ResponseBusinessSeconds { get; set; }
    public DateTimeOffset? ResponseCompletedAt { get; set; }
    public DateTimeOffset? ResolutionCompletedAt { get; set; }
    public bool ResponseWarningRaised { get; set; }
    public bool ResolutionWarningRaised { get; set; }
    public bool ResponseBreached { get; set; }
    public bool ResolutionBreached { get; set; }
}
