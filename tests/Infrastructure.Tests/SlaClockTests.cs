using Modules.Helpdesk.Data;
using Modules.Helpdesk.Features.Sla;

namespace Infrastructure.Tests;

/// <summary>
/// Where a ticket stands against its resolution target, which is the number a blast radius triages on
/// (WP-5.2). No database — the SLA row and the instant are both handed in.
/// </summary>
public sealed class SlaClockTests
{
    private static readonly BusinessHoursCalendar Calendar = new()
    {
        TimeZoneId = "UTC", WorkingDays = BusinessDays.Weekdays,
        StartTime = new TimeOnly(9, 0), EndTime = new TimeOnly(17, 0),
    };

    /// <summary>A Friday inside business hours, so the arithmetic below is not about the weekend.</summary>
    private static readonly DateTimeOffset Now = new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Exposure_WithTimeStillInHand_IsNeitherBreachedNorAtRisk()
    {
        var exposure = SlaClock.Exposure(Sla(elapsedMinutes: 60, targetMinutes: 240), Now);

        Assert.False(exposure.Breached);
        Assert.False(exposure.AtRisk);
        Assert.Equal(TimeSpan.FromMinutes(180).TotalSeconds, exposure.RemainingSeconds);
        Assert.Equal(Now.AddMinutes(180), exposure.ResolutionDueAt);
    }

    /// <summary>
    /// "At risk" is the policy's own warning line, so this and an SLA warning notification can never
    /// disagree about which tickets are close to the edge.
    /// </summary>
    [Fact]
    public void Exposure_PastTheWarningPercentButNotTheTarget_IsAtRisk()
    {
        var exposure = SlaClock.Exposure(Sla(elapsedMinutes: 200, targetMinutes: 240), Now);

        Assert.True(exposure.AtRisk);
        Assert.False(exposure.Breached);
    }

    /// <summary>A breached ticket is not also at risk: it is past the thing being risked.</summary>
    [Fact]
    public void Exposure_PastTheTarget_IsBreachedAndNotAlsoAtRisk()
    {
        var exposure = SlaClock.Exposure(Sla(elapsedMinutes: 300, targetMinutes: 240), Now);

        Assert.True(exposure.Breached);
        Assert.False(exposure.AtRisk);
        Assert.Equal(0, exposure.RemainingSeconds);
    }

    /// <summary>
    /// The failure path the stored flags would get wrong: the clock has run past the target but the
    /// scheduler has not swept yet, so <c>ResolutionBreached</c> is still false. A blast radius read
    /// between two passes must report the breach that has already happened.
    /// </summary>
    [Fact]
    public void Exposure_WhenTheClockHasPassedTheTargetButTheSchedulerHasNotSwept_StillReportsTheBreach()
    {
        var sla = Sla(elapsedMinutes: 300, targetMinutes: 240);
        sla.ResolutionBreached = false;
        sla.ResolutionWarningRaised = false;

        Assert.True(SlaClock.Exposure(sla, Now).Breached);
    }

    /// <summary>
    /// A paused SLA has banked what it used and is not accruing more, so a ticket parked on Pending
    /// overnight does not silently breach while nobody could have worked it.
    /// </summary>
    [Fact]
    public void Exposure_WhileTheSlaIsPaused_StopsConsumingTheTarget()
    {
        var sla = Sla(elapsedMinutes: 60, targetMinutes: 240);
        sla.ActiveSince = null;

        Assert.Equal(TimeSpan.FromMinutes(60).TotalSeconds, SlaClock.ElapsedSeconds(sla, Now.AddDays(3)));
        Assert.False(SlaClock.Exposure(sla, Now.AddDays(3)).Breached);
    }

    /// <summary>
    /// Work that is finished is not exposure, however long it sat before somebody finished it. Counting
    /// a resolved ticket's overrun would make every blast radius report the estate's whole history.
    /// </summary>
    [Fact]
    public void Exposure_ForAnSlaThatWasAlreadyResolved_IsNeitherBreachedNorAtRisk()
    {
        var sla = Sla(elapsedMinutes: 900, targetMinutes: 240);
        sla.ResolutionCompletedAt = Now.AddHours(-1);

        var exposure = SlaClock.Exposure(sla, Now);

        Assert.False(exposure.Breached);
        Assert.False(exposure.AtRisk);
    }

    /// <summary>
    /// The due date is walked through the calendar, not added to the wall clock: four business hours
    /// left at Friday noon lands on Monday morning rather than Friday teatime.
    /// </summary>
    [Fact]
    public void DueAt_WhenTheRemainingTimeRunsPastCloseOfBusiness_LandsOnTheNextWorkingDay()
    {
        var sla = Sla(elapsedMinutes: 0, targetMinutes: 480);

        Assert.Equal(new DateTimeOffset(2026, 8, 17, 12, 0, 0, TimeSpan.Zero), SlaClock.DueAt(sla, Now, 480 * 60d));
    }

    /// <summary>
    /// A target already overspent is due immediately rather than in the past: the question a due date
    /// answers is "when must this be done by", and there is no answer before now.
    /// </summary>
    [Fact]
    public void DueAt_ForATargetAlreadyOverspent_IsNowRatherThanAPastInstant()
    {
        var sla = Sla(elapsedMinutes: 600, targetMinutes: 240);

        Assert.Equal(Now, SlaClock.DueAt(sla, Now, 240 * 60d));
    }

    /// <summary>
    /// An SLA running since <c>Now - elapsed</c>, inside one business day so the banked time and the
    /// wall clock agree and the assertions above are about the exposure rather than about the calendar.
    /// </summary>
    private static TicketSla Sla(int elapsedMinutes, int targetMinutes) => new()
    {
        Id = Guid.CreateVersion7(),
        TicketId = Guid.CreateVersion7(),
        StartedAt = Now.AddMinutes(-elapsedMinutes),
        ActiveSince = Now,
        AccumulatedBusinessSeconds = TimeSpan.FromMinutes(elapsedMinutes).TotalSeconds,
        Policy = new SlaPolicy
        {
            Id = Guid.CreateVersion7(),
            Name = "Standard",
            ResponseTargetMinutes = 30,
            ResolutionTargetMinutes = targetMinutes,
            WarningPercent = 80,
            Calendar = Calendar,
        },
    };
}
