using Modules.Helpdesk.Data;

namespace Modules.Helpdesk.Features.Sla;

public static class BusinessTimeCalculator
{
    public static TimeSpan Elapsed(
        DateTimeOffset from, DateTimeOffset to, BusinessHoursCalendar calendar)
    {
        if (to <= from) return TimeSpan.Zero;
        var zone = TimeZoneInfo.FindSystemTimeZoneById(calendar.TimeZoneId);
        var firstDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(from, zone).DateTime);
        var lastDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(to, zone).DateTime);
        var seconds = 0d;
        for (var date = firstDate; date <= lastDate; date = date.AddDays(1))
        {
            if (!IsWorkingDay(date.DayOfWeek, calendar.WorkingDays)) continue;
            var windowStart = ToUtc(date, calendar.StartTime, zone);
            var windowEnd = ToUtc(date, calendar.EndTime, zone);
            var overlapStart = from > windowStart ? from : windowStart;
            var overlapEnd = to < windowEnd ? to : windowEnd;
            if (overlapEnd > overlapStart) seconds += (overlapEnd - overlapStart).TotalSeconds;
        }

        return TimeSpan.FromSeconds(seconds);
    }

    public static DateTimeOffset Add(
        DateTimeOffset from, TimeSpan businessTime, BusinessHoursCalendar calendar)
    {
        if (businessTime <= TimeSpan.Zero) return from;
        var zone = TimeZoneInfo.FindSystemTimeZoneById(calendar.TimeZoneId);
        var cursor = from;
        var remaining = businessTime.TotalSeconds;
        for (var guard = 0; guard < 3660; guard++)
        {
            var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(cursor, zone).DateTime);
            if (IsWorkingDay(date.DayOfWeek, calendar.WorkingDays))
            {
                var windowStart = ToUtc(date, calendar.StartTime, zone);
                var windowEnd = ToUtc(date, calendar.EndTime, zone);
                var usableStart = cursor > windowStart ? cursor : windowStart;
                if (usableStart < windowEnd)
                {
                    var available = (windowEnd - usableStart).TotalSeconds;
                    if (remaining <= available) return usableStart.AddSeconds(remaining);
                    remaining -= available;
                }
            }

            var nextDate = date.AddDays(1);
            cursor = ToUtc(nextDate, TimeOnly.MinValue, zone);
        }

        throw new InvalidOperationException("Business-time calculation exceeded ten years.");
    }

    private static DateTimeOffset ToUtc(DateOnly date, TimeOnly time, TimeZoneInfo zone)
    {
        var local = DateTime.SpecifyKind(date.ToDateTime(time), DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local)) local = local.AddHours(1);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone), TimeSpan.Zero);
    }

    private static bool IsWorkingDay(DayOfWeek day, BusinessDays days) =>
        days.HasFlag(day switch
        {
            DayOfWeek.Monday => BusinessDays.Monday,
            DayOfWeek.Tuesday => BusinessDays.Tuesday,
            DayOfWeek.Wednesday => BusinessDays.Wednesday,
            DayOfWeek.Thursday => BusinessDays.Thursday,
            DayOfWeek.Friday => BusinessDays.Friday,
            DayOfWeek.Saturday => BusinessDays.Saturday,
            DayOfWeek.Sunday => BusinessDays.Sunday,
            _ => BusinessDays.None,
        });
}
