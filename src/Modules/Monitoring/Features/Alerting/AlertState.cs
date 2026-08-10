using Modules.Monitoring.Data;

namespace Modules.Monitoring.Features.Alerting;

/// <summary>
/// Everything the state machine remembers about one rule between two readings. Held in Redis
/// (ARCHITECTURE §5 — alert state machines) and rebuilt from the open alert row when Redis has
/// forgotten, so a flush costs counters rather than correctness.
/// </summary>
/// <param name="Severity">
/// What the rule <em>is</em>, as opposed to what anyone has been told. These differ while the rule is
/// suppressed, and the difference is exactly what gets published when suppression lifts.
/// </param>
/// <param name="PublishedSeverity">The last severity an <c>AlertRaised</c>/<c>AlertCleared</c> reported.</param>
/// <param name="Candidate">
/// A severity that has been observed but not yet believed. The "for N cycles" rule is this field
/// plus <paramref name="CandidateCount"/> and nothing else.
/// </param>
/// <param name="Flips">
/// When the rule last changed between good and bad, newest last, pruned to the flap window. The flap
/// detector's entire memory.
/// </param>
/// <param name="FlappingUntil">When the current flap suppression expires; null when not flapping.</param>
public sealed record AlertState(
    AlertSeverity Severity = AlertSeverity.Ok,
    AlertSeverity PublishedSeverity = AlertSeverity.Ok,
    Guid? AlertId = null,
    AlertSeverity? Candidate = null,
    int CandidateCount = 0,
    int ConsecutiveBreaches = 0,
    DateTimeOffset? RaisedAt = null,
    DateTimeOffset? LastObservedAt = null,
    double? LastValue = null,
    IReadOnlyList<DateTimeOffset>? Flips = null,
    DateTimeOffset? FlappingUntil = null)
{
    public IReadOnlyList<DateTimeOffset> Flips { get; init; } = Flips ?? [];

    /// <summary>
    /// The five-state view the WP names. Recovering is not stored: it is precisely "an alert is open
    /// and a run of good readings has started but has not finished", which the candidate already
    /// says. Storing it separately would give two facts that can disagree.
    /// </summary>
    public AlertPhase Phase => Severity switch
    {
        AlertSeverity.Ok => AlertPhase.Ok,
        _ when Candidate is AlertSeverity.Ok => AlertPhase.Recovering,
        AlertSeverity.Warning => AlertPhase.Warning,
        _ => AlertPhase.Critical,
    };

    public bool IsFlapping(DateTimeOffset now) => FlappingUntil is { } until && now < until;
}

/// <summary>The WP's OK → Warning → Critical → Recovering → OK, derived rather than stored.</summary>
public enum AlertPhase
{
    Ok,
    Warning,
    Critical,
    Recovering,
}
