using Modules.Helpdesk.Data;

using Platform.Integration;

namespace Modules.Helpdesk.Features.Sla;

/// <summary>
/// Where one ticket stands against its SLA, measured at an instant. Pure: no database, no clock of its
/// own, no configuration — the caller supplies <c>now</c>.
/// <para>
/// It exists because WP-5.2 asks the same question of a whole outage at once that
/// <see cref="SlaService"/> already asks of one ticket, and a second copy of this arithmetic is exactly
/// the kind of duplication that drifts: the pause accounting below is subtle enough that two copies
/// would eventually disagree about a paused ticket, and the two answers would appear on the same screen.
/// </para>
/// </summary>
public static class SlaClock
{
    /// <summary>
    /// Business seconds this SLA has consumed by <paramref name="now"/>: what it banked while running,
    /// plus what has passed since it was last resumed. A paused SLA has no <c>ActiveSince</c> and its
    /// elapsed time therefore stops moving, which is the whole point of pausing one.
    /// </summary>
    public static double ElapsedSeconds(TicketSla sla, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(sla);
        return sla.AccumulatedBusinessSeconds
            + (sla.ActiveSince is null
                ? 0
                : BusinessTimeCalculator.Elapsed(sla.ActiveSince.Value, now, sla.Policy.Calendar).TotalSeconds);
    }

    /// <summary>
    /// The wall-clock instant the target falls due, found by walking the remaining business time forward
    /// through the calendar. A target already overspent is due immediately rather than in the past — the
    /// question a due date answers is "when must this be done by", and there is no answer before now.
    /// </summary>
    public static DateTimeOffset DueAt(TicketSla sla, DateTimeOffset now, double targetSeconds)
    {
        ArgumentNullException.ThrowIfNull(sla);
        var remaining = Math.Max(0, targetSeconds - sla.AccumulatedBusinessSeconds);
        return BusinessTimeCalculator.Add(
            sla.ActiveSince ?? now, TimeSpan.FromSeconds(remaining), sla.Policy.Calendar);
    }

    /// <summary>
    /// This ticket's resolution exposure as a blast radius reports it.
    /// <para>
    /// Read from the clock rather than from <see cref="TicketSla.ResolutionBreached"/>, deliberately. That
    /// flag is set by the scheduler's pass and is the audited record of when a breach was *declared*; a
    /// blast radius asked between two passes would read a breach that has already happened as time still
    /// in hand. Both numbers are true of different questions, and the one an operator triages on is this
    /// one.
    /// </para>
    /// <para>
    /// A resolved SLA is not exposure: its clock stopped when somebody finished the work, so it reports
    /// neither breach nor risk however long the ticket sat before that.
    /// </para>
    /// </summary>
    public static SlaExposure Exposure(TicketSla sla, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(sla);
        var target = sla.Policy.ResolutionTargetMinutes * 60d;
        var elapsed = ElapsedSeconds(sla, now);
        var settled = sla.ResolutionCompletedAt is not null;
        var breached = !settled && elapsed >= target;

        return new SlaExposure(
            sla.Policy.Name,
            DueAt(sla, now, target),
            Math.Max(0, target - elapsed),
            breached,
            // "At risk" is the policy's own warning line, so a blast-radius panel and an SLA warning
            // notification never disagree about which tickets are close to the edge. A breached ticket
            // is not also at risk: it is past the thing being risked.
            AtRisk: !settled && !breached && elapsed >= target * sla.Policy.WarningPercent / 100d);
    }
}
