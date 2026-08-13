using System.Globalization;

using Contracts.Events;

using Modules.Monitoring.Data;
using Modules.Monitoring.Features.Interfaces;

namespace Modules.Monitoring.Features.Alerting;

/// <summary>
/// The rules an interface check has, one pair per interface it reported.
/// <para>
/// This is the one place the platform has rules it did not get from a configuration row.
/// <see cref="AlertRules"/> derives a check's rules from the check: an ICMP check has an
/// availability rule and, where somebody set a threshold, one rule on one metric. An interface check
/// cannot work that way — nobody configures the ports, the device reports them, and a switch that
/// gains an uplink next month must alert on it without anybody editing anything. So the rules are
/// derived from the reading: whichever interfaces this poll described are the interfaces that have
/// rules this cycle.
/// </para>
/// <para>
/// Two per interface, and they answer different questions. <b>oper-status</b> needs no
/// configuration — an interface that is administratively up and operationally down is a fault on
/// every estate, so there is no threshold to set. <b>utilisation</b> uses the check's own warning and
/// critical thresholds, which is why an interface check's thresholds mean "percent of link speed"
/// and <see cref="AlertRules.PrimaryMetric"/> deliberately returns null for one: the numbers are
/// consumed here, per port, rather than once for the check.
/// </para>
/// <para>
/// Note what this cannot do, and that it is a property rather than a gap: a failed check carries no
/// samples, so a switch that has stopped answering SNMP produces no interface observations at all —
/// its availability rule fires and its forty-eight ports stay quiet. An interface is only ever
/// reported down by a device that is up enough to say so.
/// </para>
/// </summary>
public static class InterfaceAlertRules
{
    /// <summary>An interface that is meant to be up and is not.</summary>
    public const string OperStatusRule = "oper-status";

    /// <summary>The busier direction against the check's thresholds.</summary>
    public const string UtilisationRule = "utilisation";

    /// <summary>
    /// The rule id shape: <c>check:{checkId}:if:{ifIndex}:{rule}</c>.
    /// <para>
    /// The check's id rather than the device's, so deleting the check takes its alerts with it the
    /// same way every other rule's do, and the <c>:if:</c> segment so no interface rule can ever
    /// collide with the <c>check:{checkId}:{metric}</c> a threshold rule uses.
    /// </para>
    /// </summary>
    public static string RuleId(Guid checkId, int ifIndex, string rule) =>
        string.Create(CultureInfo.InvariantCulture, $"check:{checkId}:if:{ifIndex}:{rule}");

    /// <summary>
    /// Which interface rules this result has readings for, before anything is judged.
    /// <para>
    /// Needed separately from <see cref="Observe"/> because the engine loads each rule's stored state
    /// first: hysteresis reads a value differently depending on how bad the rule already is, so
    /// assessing before loading would apply the margin one cycle late.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> RuleIds(DeviceCheckResult result, CheckDefinition check)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(check);

        var rules = new List<string>();
        foreach (var reading in Readings(result))
        {
            if (reading.OperStatus is not null)
            {
                rules.Add(RuleId(check.Id, reading.IfIndex, OperStatusRule));
            }

            if (reading.Utilisation is not null && ThresholdEvaluator.HasThreshold(check))
            {
                rules.Add(RuleId(check.Id, reading.IfIndex, UtilisationRule));
            }
        }

        return rules;
    }

    /// <summary>The readings this result produces, one per rule <see cref="RuleIds"/> named.</summary>
    public static IReadOnlyList<AlertObservation> Observe(
        DeviceCheckResult result,
        CheckDefinition check,
        AlertPolicy policy,
        IReadOnlyDictionary<string, AlertState> state)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(check);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(state);

        var observations = new List<AlertObservation>();
        if (!result.Succeeded)
        {
            return observations;
        }

        foreach (var reading in Readings(result))
        {
            if (reading.OperStatus is { } operStatus)
            {
                observations.Add(OperStatusObservation(result, check, reading, operStatus));
            }

            if (reading.Utilisation is { } utilisation && ThresholdEvaluator.HasThreshold(check))
            {
                observations.Add(UtilisationObservation(result, check, reading, utilisation, policy, state));
            }
        }

        return observations;
    }

    private static AlertObservation OperStatusObservation(
        DeviceCheckResult result,
        CheckDefinition check,
        InterfaceReading reading,
        double operStatus)
    {
        // An interface somebody has shut is not a fault, and an estate has plenty of them: a switch
        // ships with every unused port down, and alerting on those would bury the one uplink that
        // matters under forty-seven ports nobody patched. An agent that does not report ifAdminStatus
        // at all is read as "meant to be up", because the alternative is a device whose interfaces
        // can never alert.
        var shutByAnOperator = Status(reading.AdminStatus) is InterfaceStatus.Down or InterfaceStatus.Testing;
        var down = !shutByAnOperator
            && Status(operStatus) is InterfaceStatus.Down or InterfaceStatus.LowerLayerDown;

        var label = Label(reading);
        return new AlertObservation(
            result.DeviceId,
            result.CiId,
            result.CheckId,
            check.Name,
            RuleId(check.Id, reading.IfIndex, OperStatusRule),
            InterfaceMetricNames.For(reading.IfIndex, InterfaceMetricNames.OperStatus),
            result.ObservedAt,
            down ? AlertSeverity.Critical : AlertSeverity.Ok,
            operStatus,
            Threshold: null,
            Summary: down
                ? $"{label} on {result.Address} is down."
                : $"{label} on {result.Address} is {Describe(Status(operStatus))}.");
    }

    private static AlertObservation UtilisationObservation(
        DeviceCheckResult result,
        CheckDefinition check,
        InterfaceReading reading,
        double utilisation,
        AlertPolicy policy,
        IReadOnlyDictionary<string, AlertState> state)
    {
        var ruleId = RuleId(check.Id, reading.IfIndex, UtilisationRule);
        var current = state.TryGetValue(ruleId, out var stored) ? stored.Severity : AlertSeverity.Ok;
        var severity = ThresholdEvaluator.Assess(
            utilisation,
            check.WarningThreshold,
            check.CriticalThreshold,
            check.Comparison,
            current,
            policy.HysteresisPercent);

        var label = Label(reading);
        var measured = string.Create(CultureInfo.InvariantCulture, $"{utilisation:0.#}%");
        // A rising comparison is what an interface threshold means, but the check carries whichever
        // one an operator set and a summary that named the wrong direction would be a lie about the
        // rule that fired.
        var direction = check.Comparison is ThresholdComparison.GreaterThan ? "at or above" : "at or below";
        var summary = severity is AlertSeverity.Ok
            ? $"{label} on {result.Address} is {measured} utilised, within thresholds."
            : string.Create(
                CultureInfo.InvariantCulture,
                $"{label} on {result.Address} is {measured} utilised, {direction} the "
                + $"{severity.ToString().ToLowerInvariant()} threshold of "
                + $"{ThresholdEvaluator.CrossedThreshold(check, severity):0.##}%.");

        return new AlertObservation(
            result.DeviceId,
            result.CiId,
            result.CheckId,
            check.Name,
            ruleId,
            InterfaceMetricNames.For(reading.IfIndex, InterfaceMetricNames.Utilisation),
            result.ObservedAt,
            severity,
            utilisation,
            ThresholdEvaluator.CrossedThreshold(check, severity),
            summary);
    }

    /// <summary>
    /// The interface's name as an alert should say it — "Gi0/1 (uplink to core)".
    /// <para>
    /// The alias is in brackets because it is the half a human wrote: an operator reading a ticket
    /// at three in the morning wants to know what the cable goes to before they want to know which
    /// port it is in. An interface the device never named falls back to its index, which is at least
    /// something to type into the switch.
    /// </para>
    /// </summary>
    private static string Label(InterfaceReading reading)
    {
        var name = reading.Name ?? string.Create(CultureInfo.InvariantCulture, $"Interface {reading.IfIndex}");
        return reading.Alias is { Length: > 0 } alias ? $"{name} ({alias})" : name;
    }

    /// <summary>
    /// An IF-MIB status number as the enum. Anything the MIB does not define — including a value too
    /// large to be one — is unknown, which is neither up nor down and therefore never alerts.
    /// </summary>
    private static InterfaceStatus Status(double? value) =>
        value is { } number && double.IsFinite(number)
        && number is >= 0 and <= 7 && Enum.IsDefined(typeof(InterfaceStatus), (int)number)
            ? (InterfaceStatus)(int)number
            : InterfaceStatus.Unknown;

    private static string Describe(InterfaceStatus status) => status switch
    {
        InterfaceStatus.Up => "up",
        InterfaceStatus.Down => "down",
        InterfaceStatus.Testing => "in test",
        InterfaceStatus.Dormant => "dormant",
        InterfaceStatus.NotPresent => "not present",
        InterfaceStatus.LowerLayerDown => "down below",
        _ => "in an unknown state",
    };

    /// <summary>
    /// Regroups the flat samples of one check result into one reading per interface.
    /// <para>
    /// The same fold <c>TelemetryIngestionPlanner</c> performs, and deliberately not shared with it:
    /// that one builds a durable row out of everything an interface reports, this one needs three
    /// fields and a name and must not depend on a row having been written first. The alert engine
    /// and the ingestion consumer sit on separate queues by WP-3.5's design, so an engine that read
    /// the interface table would be judging whatever the other consumer had managed to store.
    /// </para>
    /// </summary>
    private static IEnumerable<InterfaceReading> Readings(DeviceCheckResult result)
    {
        var readings = new SortedDictionary<int, InterfaceReading>();
        foreach (var sample in result.Metrics)
        {
            if (!InterfaceMetricNames.TryParse(sample.Name, out var ifIndex, out var field))
            {
                continue;
            }

            var reading = readings.TryGetValue(ifIndex, out var existing)
                ? existing
                : new InterfaceReading(ifIndex);
            readings[ifIndex] = field switch
            {
                InterfaceMetricNames.Name => reading with { Name = sample.Text?.Trim() },
                InterfaceMetricNames.Alias => reading with { Alias = sample.Text?.Trim() },
                InterfaceMetricNames.OperStatus => reading with { OperStatus = sample.Value },
                InterfaceMetricNames.AdminStatus => reading with { AdminStatus = sample.Value },
                InterfaceMetricNames.Utilisation => reading with { Utilisation = sample.Value },
                _ => reading,
            };
        }

        return readings.Values;
    }

    private sealed record InterfaceReading(
        int IfIndex,
        string? Name = null,
        string? Alias = null,
        double? OperStatus = null,
        double? AdminStatus = null,
        double? Utilisation = null);
}
