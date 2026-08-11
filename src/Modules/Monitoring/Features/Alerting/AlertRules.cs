using System.Globalization;
using System.Text.Json;

using Contracts.Events;

using Modules.Monitoring.Data;

namespace Modules.Monitoring.Features.Alerting;

/// <summary>One reading, addressed to one rule. What the state machine is fed.</summary>
/// <param name="RuleId">Stable across restarts and identical on a recurrence — see <see cref="AlertRules"/>.</param>
/// <param name="Severity">Already judged, hysteresis included, against the state it was read with.</param>
/// <summary>
/// The rules a check has. <see cref="Threshold"/> and <see cref="MetricName"/> are both null or both
/// set: a threshold rule without a metric to read has nothing to compare.
/// </summary>
public sealed record CheckRuleIds(string Availability, string? Threshold, string? MetricName);

public sealed record AlertObservation(
    Guid DeviceId,
    Guid CiId,
    Guid CheckId,
    string CheckName,
    string RuleId,
    string MetricName,
    DateTimeOffset ObservedAt,
    AlertSeverity Severity,
    double? Value,
    double? Threshold,
    string Summary);

/// <summary>
/// Turns a check result into the readings its rules care about. Pure, so "which rules does a failed
/// SNMP check produce" is answerable without a poller, a device or a broker.
/// <para>
/// A check produces at most two rules, and they answer different questions:
/// </para>
/// <list type="bullet">
/// <item><b>availability</b> — did the check complete at all. Every check has one, needs no
/// configuration, and is what makes a device that stopped answering an alert rather than a gap.</item>
/// <item><b>threshold</b> — is the check's primary number past its warning or critical line. Only
/// exists where the operator configured a threshold, and only for the one metric the check is
/// <em>about</em>: an SNMP CPU check reports per-core figures too, and alerting on each of them
/// separately would turn one busy host into nine alerts.</item>
/// </list>
/// <para>
/// There is deliberately no device-level reachability rule, and
/// <see cref="DeviceReachabilityChanged"/> is deliberately not consumed. It is edge-triggered — one
/// message per transition — so a "for N cycles" condition cannot be evaluated against it without
/// re-deriving the cycles it does not carry. The ICMP check's availability rule answers the same
/// question from the telemetry that arrives every cycle, which is the shape this WP's rules need.
/// </para>
/// </summary>
public static class AlertRules
{
    /// <summary>The metric an availability rule is recorded against — WP-3.4's derived 0/1 series.</summary>
    public const string AvailabilityMetric = "check.success";

    public static string AvailabilityRuleId(Guid checkId) =>
        string.Create(CultureInfo.InvariantCulture, $"check:{checkId}:availability");

    public static string ThresholdRuleId(Guid checkId, string metricName) =>
        string.Create(CultureInfo.InvariantCulture, $"check:{checkId}:{metricName}");

    /// <summary>
    /// Which rules a check has, without needing a reading. The engine loads their state before it
    /// judges anything, because hysteresis reads a value differently depending on how bad the rule
    /// already is — assessing first and loading afterwards would apply the margin one cycle late.
    /// </summary>
    public static CheckRuleIds RuleIds(CheckDefinition check)
    {
        ArgumentNullException.ThrowIfNull(check);

        var availability = AvailabilityRuleId(check.Id);
        if (!ThresholdEvaluator.HasThreshold(check)
            || PrimaryMetric(check.Type, Parameters(check)) is not { } metricName)
        {
            return new CheckRuleIds(availability, null, null);
        }

        return new CheckRuleIds(availability, ThresholdRuleId(check.Id, metricName), metricName);
    }

    /// <summary>
    /// The one number a check is about. A check reports several metrics — an SNMP CPU read carries a
    /// figure per core beside the average, and an ICMP check carries packet counts beside the round
    /// trip — but a check has exactly one warning and one critical threshold, so exactly one of its
    /// metrics can be what those thresholds mean.
    /// <para>
    /// Returns null for a check whose metrics are all text (SNMP <c>sysinfo</c>), which therefore has
    /// no threshold rule however it is configured.
    /// </para>
    /// </summary>
    public static string? PrimaryMetric(CheckType type, IReadOnlyDictionary<string, string> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        switch (type)
        {
            case CheckType.Icmp:
                return "icmp.rtt_ms";

            case CheckType.Snmp:
                var family = Parameter(parameters, "metric")?.ToLowerInvariant() ?? "sysinfo";
                return family switch
                {
                    "cpu" => "cpu.utilisation_percent",
                    "memory" => "memory.used_percent",
                    // A raw OID is named by whoever configured it, and the poller falls back to the
                    // OID itself — the same rule, so the two cannot disagree about the metric's name.
                    "oid" => Parameter(parameters, "metricName")
                        ?? (Parameter(parameters, "oid") is { } oid ? $"snmp.{oid}" : null),
                    _ => null,
                };

            // A TCP connect and an HTTP request measure how long they took and nothing else, so the
            // latency WP-3.4 derives from every check result is the number their thresholds are about.
            case CheckType.Tcp:
            case CheckType.Http:
                return "check.latency_ms";

            // A TLS check is about the certificate, not the handshake: how long the connection took
            // is not what anybody sets a threshold on. Falling, so its thresholds read 30 and 7 with
            // a LessThan comparison.
            case CheckType.Tls:
                return "tls.days_to_expiry";

            default:
                return null;
        }
    }

    /// <summary>
    /// The readings one check result produces. <paramref name="state"/> is the rule's current state,
    /// needed because hysteresis judges a value differently depending on how bad things already are.
    /// </summary>
    public static IReadOnlyList<AlertObservation> Observe(
        DeviceCheckResult result,
        CheckDefinition check,
        CheckRuleIds rules,
        AlertPolicy policy,
        IReadOnlyDictionary<string, AlertState> state)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(check);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(state);

        var observations = new List<AlertObservation>(2)
        {
            new(
                result.DeviceId,
                result.CiId,
                result.CheckId,
                result.CheckName,
                rules.Availability,
                AvailabilityMetric,
                result.ObservedAt,
                result.Succeeded ? AlertSeverity.Ok : AlertSeverity.Critical,
                result.Succeeded ? 1d : 0d,
                Threshold: null,
                Summary: result.Succeeded
                    ? $"{check.Name} on {result.Address} is completing."
                    : EndSentence(
                        $"{check.Name} on {result.Address} is failing: "
                        + (result.Error ?? "no reason reported"))),
        };

        // A failed check carries no samples, so its threshold rule simply does not advance this
        // cycle. That is the honest reading: nobody measured the CPU of a switch that did not answer,
        // and treating "unmeasured" as "fine" would clear an alert the moment a device went away.
        if (!result.Succeeded || rules.Threshold is not { } ruleId || rules.MetricName is not { } metricName)
        {
            return observations;
        }

        var sample = result.Metrics.FirstOrDefault(
            metric => metric.Value is not null
                && string.Equals(metric.Name, metricName, StringComparison.Ordinal));
        if (sample?.Value is not { } value)
        {
            // Configured with a threshold on a metric this run did not produce. Not an error and not
            // an alert — the check succeeded, it just did not report that number this time.
            return observations;
        }

        var current = state.TryGetValue(ruleId, out var stored) ? stored.Severity : AlertSeverity.Ok;
        var severity = ThresholdEvaluator.Assess(
            value,
            check.WarningThreshold,
            check.CriticalThreshold,
            check.Comparison,
            current,
            policy.HysteresisPercent);

        observations.Add(new AlertObservation(
            result.DeviceId,
            result.CiId,
            result.CheckId,
            result.CheckName,
            ruleId,
            metricName,
            result.ObservedAt,
            severity,
            value,
            ThresholdEvaluator.CrossedThreshold(check, severity),
            Summarise(check, metricName, value, severity, sample.Unit)));

        return observations;
    }

    /// <summary>
    /// Terminates a sentence exactly once.
    /// <para>
    /// An availability summary ends with the reason the poller reported, and the poller writes whole
    /// sentences — "No reply from 192.0.2.1 after 3 packets." — so appending a full stop
    /// unconditionally produced "…after 3 packets.." on every failing check in the estate. Left
    /// alone when the reason is already punctuated, added when it is not, because a reason that runs
    /// into whatever follows it reads worse than either.
    /// </para>
    /// </summary>
    private static string EndSentence(string text)
    {
        var trimmed = text.TrimEnd();
        return trimmed.Length > 0 && trimmed[^1] is '.' or '!' or '?'
            ? trimmed
            : trimmed + ".";
    }

    private static string Summarise(
        CheckDefinition check,
        string metricName,
        double value,
        AlertSeverity severity,
        string? unit)
    {
        var reading = string.Create(CultureInfo.InvariantCulture, $"{value:0.##}{unit}");
        if (severity is AlertSeverity.Ok)
        {
            return $"{check.Name}: {metricName} is {reading}, within thresholds.";
        }

        var threshold = ThresholdEvaluator.CrossedThreshold(check, severity);
        var direction = check.Comparison is ThresholdComparison.GreaterThan ? "at or above" : "at or below";
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{check.Name}: {metricName} is {reading}, {direction} the {severity.ToString().ToLowerInvariant()} threshold of {threshold:0.##}.");
    }

    /// <summary>
    /// The check's parameters, treating an unreadable document as an empty one. A check whose jsonb
    /// cannot be parsed still has an availability rule, and that matters more than the threshold rule
    /// it loses: the alternative is that one malformed row stops the whole batch being evaluated.
    /// </summary>
    private static IReadOnlyDictionary<string, string> Parameters(CheckDefinition check)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(check.ParametersJson)
                ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private static string? Parameter(IReadOnlyDictionary<string, string> parameters, string key) =>
        parameters.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : null;
}
