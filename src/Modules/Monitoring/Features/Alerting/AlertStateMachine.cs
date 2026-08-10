using Modules.Monitoring.Data;

namespace Modules.Monitoring.Features.Alerting;

/// <summary>What one reading did to a rule, beyond changing its state.</summary>
public enum AlertAction
{
    /// <summary>Nothing to tell anyone. Most readings.</summary>
    None,

    /// <summary>Publish <c>AlertRaised</c> at the transition's severity — a new alert or an escalation.</summary>
    Raise,

    /// <summary>Publish <c>AlertCleared</c>. The alert is over.</summary>
    Clear,
}

/// <summary>
/// The result of one reading: the rule's new state, and what the outside world should be told.
/// </summary>
/// <param name="SuppressedBy">
/// Why nothing is being published when the state changed anyway. Recorded on the alert row so a
/// board can say "muted" rather than showing an alert nobody was told about and no reason why.
/// </param>
public sealed record AlertTransition(
    AlertState State,
    AlertAction Action,
    AlertSeverity Severity,
    AlertSuppression SuppressedBy);

/// <summary>
/// The core of WP-3.5, and the only place a reading becomes an alert. Pure: no clock, no Redis, no
/// database, no bus — <em>every</em> rule the WP asks for (N cycles, hysteresis via the severity it
/// is handed, flap suppression, maintenance muting, raise once, clear once) is decided here and is
/// therefore testable by calling a function in a loop.
/// <para>
/// The invariant that makes "raised exactly once" true is that publication is driven by a difference
/// between <see cref="AlertState.Severity"/> and <see cref="AlertState.PublishedSeverity"/>, never by
/// the transition that produced it. A reading that repeats what is already true changes neither, so
/// it publishes nothing; a suppression that ends with the two out of step publishes once, whatever
/// happened in between.
/// </para>
/// </summary>
public static class AlertStateMachine
{
    /// <param name="observed">
    /// The severity this reading implies, already judged against the rule's current state by
    /// <see cref="ThresholdEvaluator"/> — hysteresis is applied there, because it is a property of
    /// how a number is read rather than of how a state advances.
    /// </param>
    /// <param name="muted">True when an active maintenance window covers the device.</param>
    /// <param name="newAlertId">
    /// The id to use if this reading opens an alert. Passed in rather than generated here so this
    /// function stays a function: the same inputs give the same outputs, and a test can assert on the
    /// id it handed over.
    /// </param>
    public static AlertTransition Advance(
        AlertState state,
        AlertSeverity observed,
        DateTimeOffset observedAt,
        double? value,
        AlertPolicy policy,
        bool muted,
        Guid newAlertId)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(policy);

        var (candidate, candidateCount) = TrackCandidate(state, observed);
        var confirmed = candidate is { } pending
            && candidateCount >= Required(state.Severity, pending, policy);

        var severity = confirmed ? candidate!.Value : state.Severity;
        var flips = state.Flips;
        var flappingUntil = state.FlappingUntil;

        if (confirmed && CrossesTheGoodBadLine(state.Severity, severity))
        {
            // Counted on the rule's own state changes rather than on published messages: suppression
            // must not hide the evidence that suppression is needed, or a flapping rule would go
            // quiet once and then be believed again forever.
            flips = Prune([.. flips, observedAt], observedAt - policy.FlapWindow);
            if (flips.Count >= policy.FlapThreshold)
            {
                flappingUntil = observedAt + policy.FlapCooldown;
            }
        }
        else
        {
            flips = Prune(flips, observedAt - policy.FlapWindow);
        }

        var next = state with
        {
            Severity = severity,
            Candidate = confirmed ? null : candidate,
            CandidateCount = confirmed ? 0 : candidateCount,
            ConsecutiveBreaches = observed is AlertSeverity.Ok ? 0 : state.ConsecutiveBreaches + 1,
            LastObservedAt = observedAt,
            LastValue = value ?? state.LastValue,
            Flips = flips,
            FlappingUntil = flappingUntil,
        };

        // An alert row exists for as long as the rule is bad, whether or not anything was published.
        // WP-3.6 tickets off the event, but an operator's board reads the row, and a muted device
        // with no row at all is indistinguishable from a healthy one.
        if (severity is not AlertSeverity.Ok && next.AlertId is null)
        {
            next = next with { AlertId = newAlertId, RaisedAt = observedAt };
        }

        var flapping = next.IsFlapping(observedAt);
        var suppression = muted ? AlertSuppression.Maintenance
            : flapping ? AlertSuppression.Flapping
            : AlertSuppression.None;

        if (suppression is not AlertSuppression.None || severity == next.PublishedSeverity)
        {
            // Nothing published. When the suppression lifts, the two severities are still out of step
            // and the next reading publishes the difference — so a window that ends over a device
            // that is still down does not stay silent.
            //
            // The alert is forgotten only if there is one and the rule is good again with nobody left
            // to tell: an alert that was raised while suppressed and recovered while suppressed has
            // no clear to publish, but its row still has to close. Guarding on the alert id matters —
            // closing unconditionally here would also reset the candidate counter on every ordinary
            // healthy reading, and no rule would ever reach its sustain count.
            var settled = severity is AlertSeverity.Ok
                && suppression is AlertSuppression.None
                && next.AlertId is not null;

            return new AlertTransition(
                settled ? Close(next) : next,
                AlertAction.None,
                severity,
                suppression);
        }

        var action = severity is AlertSeverity.Ok ? AlertAction.Clear : AlertAction.Raise;
        var published = next with { PublishedSeverity = severity };
        return new AlertTransition(
            action is AlertAction.Clear ? Close(published) : published,
            action,
            severity,
            AlertSuppression.None);
    }

    /// <summary>
    /// Counts consecutive readings that agree on something other than the current state. A reading
    /// that agrees with the current state is not evidence of anything and resets the count — which is
    /// why a value that crosses a threshold once, then goes back, never becomes an alert.
    /// </summary>
    private static (AlertSeverity? Candidate, int Count) TrackCandidate(AlertState state, AlertSeverity observed)
    {
        if (observed == state.Severity)
        {
            return (null, 0);
        }

        return observed == state.Candidate
            ? (observed, state.CandidateCount + 1)
            : (observed, 1);
    }

    /// <summary>
    /// Getting worse takes <see cref="AlertPolicy.SustainedCycles"/>; getting better takes
    /// <see cref="AlertPolicy.RecoveryCycles"/>. Deliberately asymmetric — an alert that outlives its
    /// problem is what teaches people to ignore alerts.
    /// </summary>
    private static int Required(AlertSeverity current, AlertSeverity candidate, AlertPolicy policy) =>
        candidate > current ? policy.SustainedCycles : policy.RecoveryCycles;

    private static bool CrossesTheGoodBadLine(AlertSeverity from, AlertSeverity to) =>
        (from is AlertSeverity.Ok) != (to is AlertSeverity.Ok);

    /// <summary>Forgets the alert but keeps the flap history, which is about the rule rather than the alert.</summary>
    private static AlertState Close(AlertState state) => state with
    {
        AlertId = null,
        RaisedAt = null,
        Candidate = null,
        CandidateCount = 0,
        ConsecutiveBreaches = 0,
    };

    private static IReadOnlyList<DateTimeOffset> Prune(
        IReadOnlyList<DateTimeOffset> flips,
        DateTimeOffset cutoff)
    {
        if (flips.Count == 0 || flips[0] >= cutoff)
        {
            return flips;
        }

        return [.. flips.Where(flip => flip >= cutoff)];
    }
}
