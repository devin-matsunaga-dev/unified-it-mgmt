namespace Contracts.Events;

/// <summary>
/// A monitored rule has been in a bad state long enough to be worth waking somebody for.
/// <para>
/// Published on a change of severity, never per cycle: a rule that stays Critical for an hour says
/// so once, following the same rule as <see cref="DeviceReachabilityChanged"/> and
/// <see cref="PollerHeartbeatMissed"/>. A rule that escalates Warning → Critical publishes again at
/// the new severity, because that is news; WP-3.6 dedupes on
/// <c>alert:{deviceId}:{ruleId}</c> and annotates the ticket it already opened.
/// </para>
/// <para>
/// Nothing about maintenance windows or flapping reaches this event, because neither produces one:
/// a rule inside a window and a rule that is flapping are both evaluated and recorded, and simply
/// do not publish. A consumer of this event is therefore always looking at a real, sustained,
/// unmuted problem.
/// </para>
/// </summary>
/// <param name="AlertId">
/// The alert row this concerns. Stable across an escalation, so Warning and Critical for one problem
/// carry the same id, and a consumer that stored the first can find it again.
/// </param>
/// <param name="RuleId">
/// What is wrong, in a form that is stable across restarts and identical every time the same problem
/// recurs — <c>check:{checkId}:availability</c> or <c>check:{checkId}:{metric}</c>. Device-scoped, so
/// WP-3.6's <c>alert:{deviceId}:{ruleId}</c> is unique per problem per device.
/// </param>
/// <param name="Severity"><c>Warning</c> or <c>Critical</c>. Never <c>Ok</c> — that is a clear.</param>
/// <param name="Threshold">The configured value that was crossed; null for an availability rule, which has none.</param>
/// <param name="ConsecutiveBreaches">How many cycles in a row the rule has been bad, counting the one that raised it.</param>
public sealed record AlertRaised(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid AlertId,
    Guid DeviceId,
    Guid CiId,
    Guid CheckId,
    string RuleId,
    string CheckName,
    string Severity,
    string MetricName,
    double? Value,
    double? Threshold,
    string Summary,
    DateTimeOffset RaisedAt,
    int ConsecutiveBreaches);
