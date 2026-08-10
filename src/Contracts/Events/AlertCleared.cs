namespace Contracts.Events;

/// <summary>
/// A rule that was alerting has been good again for long enough to believe. The counterpart of
/// <see cref="AlertRaised"/>, published exactly once per alert.
/// <para>
/// "Long enough to believe" is the recovery half of the state machine: a single good reading moves
/// the rule to Recovering, and only a run of them clears it. A rule that recovers and fails again
/// inside that run never clears, which is what stops one alert becoming a pair of messages every
/// cycle.
/// </para>
/// </summary>
/// <param name="PreviousSeverity">What the alert was at when it cleared — the worst it reached is not tracked.</param>
/// <param name="DurationSeconds">
/// How long the alert was open. Carried rather than left to the consumer to subtract, because
/// <see cref="RaisedAt"/> is a fact about a row the consumer may not hold.
/// </param>
public sealed record AlertCleared(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid AlertId,
    Guid DeviceId,
    Guid CiId,
    Guid CheckId,
    string RuleId,
    string CheckName,
    string PreviousSeverity,
    string MetricName,
    double? Value,
    string Summary,
    DateTimeOffset RaisedAt,
    long DurationSeconds);
