using Modules.Monitoring.Data;
using Modules.Monitoring.Features.Alerting;

namespace Infrastructure.Tests;

/// <summary>
/// The WP's verification list, driven through the state machine itself: crossing a threshold once is
/// not an alert, a sustained problem raises exactly one, a recovery clears exactly one, a flapping
/// series is suppressed and flagged, and a maintenance window silences everything.
/// <para>
/// No database, no Redis, no broker and no clock — every one of these is a loop over a pure function,
/// which is the whole reason the decisions live where they do.
/// </para>
/// </summary>
public sealed class AlertStateMachineTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 10, 9, 0, 0, TimeSpan.Zero);

    private static readonly AlertPolicy Policy = new(
        SustainedCycles: 3,
        RecoveryCycles: 2,
        HysteresisPercent: 5,
        FlapThreshold: 4,
        FlapWindow: TimeSpan.FromMinutes(10),
        FlapCooldown: TimeSpan.FromMinutes(10));

    // ---- the N-cycle rule ----

    [Fact]
    public void Advance_ThresholdCrossedOnce_RaisesNothing()
    {
        var run = Drive([Bad, Good, Good, Good]);

        Assert.DoesNotContain(run.Actions, action => action is not AlertAction.None);
        Assert.Equal(AlertSeverity.Ok, run.State.Severity);
        Assert.Null(run.State.AlertId);
    }

    [Fact]
    public void Advance_ThresholdCrossedTwiceOfThree_StillRaisesNothing()
    {
        var run = Drive([Bad, Bad, Good]);

        Assert.DoesNotContain(run.Actions, action => action is not AlertAction.None);
        Assert.Equal(AlertSeverity.Ok, run.State.Severity);
    }

    /// <summary>
    /// The reading that meets the count raises, and every reading after it says the same thing and
    /// therefore says nothing. This is the WP's "sustained → Critical raised exactly once".
    /// </summary>
    [Fact]
    public void Advance_SustainedBreach_RaisesCriticalExactlyOnce()
    {
        var run = Drive([Bad, Bad, Bad, Bad, Bad, Bad, Bad, Bad]);

        Assert.Equal([AlertAction.Raise], run.Actions.Where(action => action is not AlertAction.None));
        Assert.Equal(AlertSeverity.Critical, run.State.Severity);
        Assert.Equal(AlertSeverity.Critical, run.State.PublishedSeverity);
        Assert.NotNull(run.State.AlertId);
        Assert.Equal(8, run.State.ConsecutiveBreaches);
    }

    /// <summary>
    /// A count that is only satisfied by <em>consecutive</em> readings. Two bad, one good, two bad is
    /// five bad-ish readings and no alert, which is exactly what a flapping link looks like before the
    /// flap policy has enough evidence to say so.
    /// </summary>
    [Fact]
    public void Advance_BreachesInterruptedByAGoodReading_RestartTheCount()
    {
        var run = Drive([Bad, Bad, Good, Bad, Bad]);

        Assert.DoesNotContain(run.Actions, action => action is not AlertAction.None);
        Assert.Equal(AlertSeverity.Ok, run.State.Severity);
    }

    /// <summary>An escalation is news, and the alert keeps its identity across it.</summary>
    [Fact]
    public void Advance_WarningThenCritical_RaisesTwiceUnderOneAlertId()
    {
        var warning = Enumerable.Repeat(AlertSeverity.Warning, 3);
        var critical = Enumerable.Repeat(AlertSeverity.Critical, 3);
        var run = Drive([.. warning, .. critical]);

        Assert.Equal(
            [AlertAction.Raise, AlertAction.Raise],
            run.Actions.Where(action => action is not AlertAction.None));
        Assert.Equal([AlertSeverity.Warning, AlertSeverity.Critical], run.RaisedSeverities);
        Assert.Single(run.AlertIds.Distinct());
        Assert.Equal(AlertSeverity.Critical, run.State.Severity);
    }

    // ---- recovery ----

    [Fact]
    public void Advance_RecoveryAfterAnAlert_ClearsExactlyOnce()
    {
        var run = Drive([Bad, Bad, Bad, Good, Good, Good, Good]);

        Assert.Equal(
            [AlertAction.Raise, AlertAction.Clear],
            run.Actions.Where(action => action is not AlertAction.None));
        Assert.Equal(AlertSeverity.Ok, run.State.Severity);
        Assert.Null(run.State.AlertId);
    }

    /// <summary>
    /// One good reading is not a recovery. This is the Recovering state in the WP's chain: the alert
    /// is still open and still Critical, and a single good sample has started a run that has not
    /// finished.
    /// </summary>
    [Fact]
    public void Advance_OneGoodReadingAfterAnAlert_IsRecoveringRatherThanCleared()
    {
        var run = Drive([Bad, Bad, Bad, Good]);

        Assert.Equal([AlertAction.Raise], run.Actions.Where(action => action is not AlertAction.None));
        Assert.Equal(AlertPhase.Recovering, run.State.Phase);
        Assert.Equal(AlertSeverity.Critical, run.State.Severity);
        Assert.NotNull(run.State.AlertId);
    }

    /// <summary>A recovery interrupted before it completes leaves the alert exactly where it was.</summary>
    [Fact]
    public void Advance_RecoveryInterrupted_KeepsTheAlertAndPublishesNothingFurther()
    {
        var run = Drive([Bad, Bad, Bad, Good, Bad, Bad]);

        Assert.Equal([AlertAction.Raise], run.Actions.Where(action => action is not AlertAction.None));
        Assert.Equal(AlertSeverity.Critical, run.State.Severity);
        Assert.Equal(AlertPhase.Critical, run.State.Phase);
    }

    // ---- flap suppression ----

    /// <summary>
    /// A series that raises and clears repeatedly. Each raise/clear pair is two state changes, so the
    /// fourth one trips the flap policy — and is itself withheld, because the change that proves a
    /// rule is flapping is exactly the kind of message the policy exists to stop. The WP's "flapping
    /// series → suppressed with flap flag".
    /// </summary>
    [Fact]
    public void Advance_FlappingSeries_IsSuppressedAndFlagged()
    {
        var run = Drive(FlappingSeries);

        Assert.True(run.State.IsFlapping(Start + (Step * (FlappingSeries.Length - 1))));
        Assert.Contains(AlertSuppression.Flapping, run.Suppressions);

        // The first raise/clear pair and the second raise got through. The clear that completed the
        // fourth state change tripped the policy, and nothing since has been published.
        Assert.Equal(
            [AlertAction.Raise, AlertAction.Clear, AlertAction.Raise],
            run.Actions.Where(action => action is not AlertAction.None));

        // Still evaluated, and the disagreement is the point: the rule is good again, the last thing
        // anyone was told was Critical, and the alert row is still open to say so.
        Assert.Equal(AlertSeverity.Ok, run.State.Severity);
        Assert.Equal(AlertSeverity.Critical, run.State.PublishedSeverity);
        Assert.NotNull(run.State.AlertId);
    }

    /// <summary>
    /// Suppression must not be permanent. A rule that flapped and then genuinely settled has an open
    /// alert nobody has been told is over; once the cooldown expires the next reading reconciles and
    /// clears it. Without this a flapping device would leave a Critical alert on the board forever.
    /// </summary>
    [Fact]
    public void Advance_AfterFlapCooldown_PublishesTheStateNobodyWasToldAbout()
    {
        var flapped = Drive(FlappingSeries);
        Assert.Equal(AlertSeverity.Critical, flapped.State.PublishedSeverity);

        // An hour later: the cooldown has expired and the flips have aged out of the window.
        var later = Start + TimeSpan.FromHours(1);
        var transition = AlertStateMachine.Advance(
            flapped.State, Good, later, value: 1, Policy, muted: false, Guid.CreateVersion7());

        Assert.Equal(AlertAction.Clear, transition.Action);
        Assert.False(transition.State.IsFlapping(later));
        Assert.Null(transition.State.AlertId);
    }

    /// <summary>Three raise/clear pairs, one reading a minute. Six state changes, threshold four.</summary>
    private static readonly AlertSeverity[] FlappingSeries =
    [
        Bad, Bad, Bad, Good, Good,
        Bad, Bad, Bad, Good, Good,
        Bad, Bad, Bad, Good, Good,
    ];

    // ---- maintenance windows ----

    [Fact]
    public void Advance_InsideAMaintenanceWindow_RaisesNothingAndRecordsWhy()
    {
        var run = Drive([Bad, Bad, Bad, Bad, Bad], muted: true);

        Assert.DoesNotContain(run.Actions, action => action is not AlertAction.None);
        Assert.All(run.Suppressions, suppression => Assert.Equal(AlertSuppression.Maintenance, suppression));

        // Muted, not blind: the rule knows it is Critical and has a row to show for it.
        Assert.Equal(AlertSeverity.Critical, run.State.Severity);
        Assert.NotNull(run.State.AlertId);
        Assert.Equal(AlertSeverity.Ok, run.State.PublishedSeverity);
    }

    /// <summary>
    /// The window ending over a device that is still down has to speak. This is the same reconciling
    /// path the flap cooldown uses, and it is why suppression withholds the publication rather than
    /// the evaluation.
    /// </summary>
    [Fact]
    public void Advance_WhenAWindowEndsOverAStillBrokenDevice_RaisesThen()
    {
        var muted = Drive([Bad, Bad, Bad, Bad], muted: true);

        var transition = AlertStateMachine.Advance(
            muted.State, Bad, Start + (Step * 4), value: 99, Policy, muted: false, Guid.CreateVersion7());

        Assert.Equal(AlertAction.Raise, transition.Action);
        Assert.Equal(AlertSeverity.Critical, transition.Severity);
        Assert.Equal(muted.State.AlertId, transition.State.AlertId);
    }

    /// <summary>A device that recovered while nobody was listening must not announce a clear.</summary>
    [Fact]
    public void Advance_WhenAWindowEndsOverARecoveredDevice_SaysNothingAtAll()
    {
        var muted = Drive([Bad, Bad, Bad, Bad, Good, Good], muted: true);
        Assert.Equal(AlertSeverity.Ok, muted.State.Severity);

        var transition = AlertStateMachine.Advance(
            muted.State, Good, Start + (Step * 6), value: 1, Policy, muted: false, Guid.CreateVersion7());

        Assert.Equal(AlertAction.None, transition.Action);
        Assert.Null(transition.State.AlertId);
    }

    // ---- per-check tuning ----

    /// <summary>
    /// The tuning is the rule. A check configured to alert on the first bad reading does, which is
    /// what makes the columns worth having rather than a platform-wide constant.
    /// </summary>
    [Fact]
    public void Advance_WithASustainCountOfOne_RaisesOnTheFirstBreach()
    {
        var policy = Policy with { SustainedCycles = 1 };

        var transition = AlertStateMachine.Advance(
            new AlertState(), Bad, Start, value: 99, policy, muted: false, Guid.CreateVersion7());

        Assert.Equal(AlertAction.Raise, transition.Action);
        Assert.Equal(AlertSeverity.Critical, transition.Severity);
    }

    // ---- failure paths ----

    [Fact]
    public void Advance_WithNoState_Throws() =>
        Assert.Throws<ArgumentNullException>(() => AlertStateMachine.Advance(
            null!, Bad, Start, value: 1, Policy, muted: false, Guid.CreateVersion7()));

    [Fact]
    public void Advance_WithNoPolicy_Throws() =>
        Assert.Throws<ArgumentNullException>(() => AlertStateMachine.Advance(
            new AlertState(), Bad, Start, value: 1, null!, muted: false, Guid.CreateVersion7()));

    // ---- driving ----

    private const AlertSeverity Bad = AlertSeverity.Critical;
    private const AlertSeverity Good = AlertSeverity.Ok;
    private static readonly TimeSpan Step = TimeSpan.FromMinutes(1);

    private sealed record Run(
        AlertState State,
        IReadOnlyList<AlertAction> Actions,
        IReadOnlyList<AlertSuppression> Suppressions,
        IReadOnlyList<AlertSeverity> RaisedSeverities,
        IReadOnlyList<Guid> AlertIds);

    /// <summary>One reading a minute, which is what a check on a fixed interval produces.</summary>
    private static Run Drive(IReadOnlyList<AlertSeverity> readings, bool muted = false)
    {
        var state = new AlertState();
        var actions = new List<AlertAction>();
        var suppressions = new List<AlertSuppression>();
        var raised = new List<AlertSeverity>();
        var alertIds = new List<Guid>();

        for (var index = 0; index < readings.Count; index++)
        {
            var transition = AlertStateMachine.Advance(
                state,
                readings[index],
                Start + (Step * index),
                value: readings[index] is AlertSeverity.Ok ? 1 : 99,
                Policy,
                muted,
                Guid.CreateVersion7());

            state = transition.State;
            actions.Add(transition.Action);
            suppressions.Add(transition.SuppressedBy);
            if (transition.Action is AlertAction.Raise)
            {
                raised.Add(transition.Severity);
                alertIds.Add(transition.State.AlertId!.Value);
            }
        }

        return new Run(state, actions, suppressions, raised, alertIds);
    }
}
