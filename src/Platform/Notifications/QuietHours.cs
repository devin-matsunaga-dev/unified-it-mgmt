using Platform.Data;

namespace Platform.Notifications;

/// <summary>
/// A daily quiet window and the two questions asked of it: is it quiet now, and when does this
/// window end. Pure, so the awkward cases — a window that crosses midnight, a time zone that is not
/// the host's, a DST transition inside the window — are unit-testable without a database or a clock.
/// </summary>
public readonly record struct QuietHoursSchedule(TimeOnly? Start, TimeOnly? End, string TimeZoneId)
{
    /// <summary>
    /// A window needs both ends. One end alone is a half-stated intention, and a schedule that
    /// guesses the other half would silence notifications nobody asked to silence.
    /// </summary>
    public bool IsConfigured => Start is not null && End is not null && Start != End;

    /// <summary>
    /// Whether <paramref name="instant"/> falls inside the window, and if so the instant the window
    /// next ends — which is when a deferred notification is released.
    /// </summary>
    public QuietHoursVerdict Evaluate(DateTimeOffset instant)
    {
        if (!IsConfigured)
        {
            return QuietHoursVerdict.NotQuiet;
        }

        var zone = ResolveZone(TimeZoneId);
        var local = TimeZoneInfo.ConvertTime(instant, zone);
        var localTime = TimeOnly.FromDateTime(local.DateTime);
        var start = Start!.Value;
        var end = End!.Value;

        // A window that wraps midnight (22:00 → 07:00) is the common one, so it is the case the
        // comparison is written for rather than the exception bolted on.
        var quiet = start < end
            ? localTime >= start && localTime < end
            : localTime >= start || localTime < end;

        if (!quiet)
        {
            return QuietHoursVerdict.NotQuiet;
        }

        // The end is today's if it is still ahead of us in local time, tomorrow's otherwise — which
        // is exactly the wrapped window seen from after midnight.
        var endDate = localTime < end ? DateOnly.FromDateTime(local.DateTime) : DateOnly.FromDateTime(local.DateTime).AddDays(1);
        return new QuietHoursVerdict(true, ToInstant(endDate, end, zone));
    }

    /// <summary>
    /// Resolves the local wall clock to an instant, taking the awkward days as they come: a time that
    /// does not exist (spring forward) is pushed past the gap, and a time that happens twice (autumn
    /// back) takes the first occurrence — so a release always lands on or after the window's end and
    /// never before it.
    /// </summary>
    private static DateTimeOffset ToInstant(DateOnly date, TimeOnly time, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(time);
        while (zone.IsInvalidTime(local))
        {
            local = local.AddMinutes(1);
        }

        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }

    /// <summary>
    /// An unknown or unusable time-zone id falls back to UTC rather than throwing. The schedule is
    /// operator-entered configuration read on the alerting path, and a typo in it must not be able to
    /// stop a Critical notification — the fallback is wrong by hours, a throw is wrong by everything.
    /// Writes validate the id, so this only ever catches a zone that has since left the system.
    /// </summary>
    public static TimeZoneInfo ResolveZone(string? timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            return TimeZoneInfo.Utc;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }

    public static bool IsKnownZone(string? timeZoneId) =>
        !string.IsNullOrWhiteSpace(timeZoneId)
        && TimeZoneInfo.TryFindSystemTimeZoneById(timeZoneId, out _);

    public static QuietHoursSchedule From(NotificationRoutingRule rule) =>
        new(rule.QuietHoursStart, rule.QuietHoursEnd, rule.TimeZone);

    public static QuietHoursSchedule From(UserNotificationPreference preference) =>
        new(preference.QuietHoursStart, preference.QuietHoursEnd, preference.TimeZone);
}

/// <param name="ReleaseAfter">When the current window ends. Null when nothing is being withheld.</param>
public readonly record struct QuietHoursVerdict(bool IsQuiet, DateTimeOffset? ReleaseAfter)
{
    public static QuietHoursVerdict NotQuiet => new(false, null);
}
