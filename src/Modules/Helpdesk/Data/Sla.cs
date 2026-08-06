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

public sealed class SlaPolicy
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public TicketPriority Priority { get; set; }
    public string? Category { get; set; }
    public int ResponseTargetMinutes { get; set; }
    public int ResolutionTargetMinutes { get; set; }
    public int WarningPercent { get; set; } = 80;
    public Guid CalendarId { get; set; }
    public BusinessHoursCalendar Calendar { get; set; } = null!;
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class TicketSla
{
    public Guid Id { get; set; }
    public Guid TicketId { get; set; }
    public Ticket Ticket { get; set; } = null!;
    public Guid PolicyId { get; set; }
    public SlaPolicy Policy { get; set; } = null!;
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
