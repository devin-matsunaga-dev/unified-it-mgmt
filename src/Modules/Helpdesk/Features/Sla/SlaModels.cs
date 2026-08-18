using Modules.Helpdesk.Data;

namespace Modules.Helpdesk.Features.Sla;

public sealed record CreateBusinessHoursCalendarRequest(
    string Name, string TimeZoneId, BusinessDays WorkingDays, TimeOnly StartTime, TimeOnly EndTime);
public sealed record BusinessHoursCalendarResponse(
    Guid Id, string Name, string TimeZoneId, BusinessDays WorkingDays, TimeOnly StartTime, TimeOnly EndTime,
    /// <summary>How many policies use it. One with any cannot be deleted.</summary>
    int PolicyCount = 0);
/// <param name="Priority">Null matches any priority — the condition is simply not applied.</param>
/// <param name="TicketType">Null matches incidents and service requests alike.</param>
/// <param name="CategoryId">Null matches any category, including tickets that have none.</param>
public sealed record SavePolicyRequest(
    string Name,
    int ResponseTargetMinutes,
    int ResolutionTargetMinutes,
    int WarningPercent,
    Guid CalendarId,
    TicketPriority? Priority = null,
    TicketType? TicketType = null,
    Guid? CategoryId = null,
    int SortOrder = 0,
    bool IsActive = true);

public sealed record SlaPolicyResponse(
    Guid Id,
    string Name,
    int SortOrder,
    TicketPriority? Priority,
    TicketType? TicketType,
    Guid? CategoryId,
    string? CategoryName,
    int ResponseTargetMinutes,
    int ResolutionTargetMinutes,
    int WarningPercent,
    Guid CalendarId,
    string CalendarName,
    bool IsActive,
    /// <summary>How many tickets have run against it. A policy with any cannot be deleted.</summary>
    int TicketCount);

public enum SlaOutcome
{
    Success,
    NotFound,
    CalendarNotFound,
    CategoryNotFound,
    InUse,
}

public sealed record SlaPolicyResult(SlaOutcome Outcome, SlaPolicyResponse? Policy = null, string? Error = null);
public sealed record SlaCalendarResult(SlaOutcome Outcome, BusinessHoursCalendarResponse? Calendar = null, string? Error = null);

/// <summary>The order to apply, as a list of policy ids. Anything absent keeps its place after them.</summary>
public sealed record ReorderPoliciesRequest(IReadOnlyList<Guid> PolicyIds);
public sealed record SlaRemainingResponse(
    Guid TicketId, string Policy, bool IsPaused, double ResponseRemainingSeconds,
    double ResolutionRemainingSeconds, DateTimeOffset ResponseDueAt, DateTimeOffset ResolutionDueAt,
    DateTimeOffset? ResponseCompletedAt, DateTimeOffset? ResolutionCompletedAt);
