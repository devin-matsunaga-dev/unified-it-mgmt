using Modules.Helpdesk.Data;

namespace Modules.Helpdesk.Features.Sla;

public sealed record CreateBusinessHoursCalendarRequest(
    string Name, string TimeZoneId, BusinessDays WorkingDays, TimeOnly StartTime, TimeOnly EndTime);
public sealed record BusinessHoursCalendarResponse(
    Guid Id, string Name, string TimeZoneId, BusinessDays WorkingDays, TimeOnly StartTime, TimeOnly EndTime);
public sealed record CreateSlaPolicyRequest(
    string Name, TicketPriority Priority, string? Category, int ResponseTargetMinutes,
    int ResolutionTargetMinutes, int WarningPercent, Guid CalendarId);
public sealed record SlaPolicyResponse(
    Guid Id, string Name, TicketPriority Priority, string? Category, int ResponseTargetMinutes,
    int ResolutionTargetMinutes, int WarningPercent, Guid CalendarId);
public sealed record SlaRemainingResponse(
    Guid TicketId, string Policy, bool IsPaused, double ResponseRemainingSeconds,
    double ResolutionRemainingSeconds, DateTimeOffset ResponseDueAt, DateTimeOffset ResolutionDueAt,
    DateTimeOffset? ResponseCompletedAt, DateTimeOffset? ResolutionCompletedAt);
