using Contracts.Events;

using Modules.Monitoring.Data;
using Modules.Monitoring.Features.Alerting;

namespace Infrastructure.Tests;

/// <summary>
/// The rules an interface check has, which are the only rules in the platform derived from a reading
/// rather than from a configuration row. What is asserted here is mostly what does <em>not</em>
/// alert: a port somebody shut, a port on a device that has stopped answering, a status the MIB does
/// not define. An estate has far more of those than it has faults, and each of them alerting would
/// make the feature unusable on the first switch it met.
/// </summary>
public sealed class InterfaceAlertRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 14, 9, 0, 0, TimeSpan.Zero);

    private static readonly AlertPolicy Policy =
        new(3, 2, 5, 4, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10));

    // ---- oper status ----

    [Fact]
    public void Observe_AnInterfaceThatIsAdministrativelyUpAndOperationallyDown_IsCritical()
    {
        var check = Check();
        var observation = Single(check, Interface(1, "Gi0/2", alias: "uplink to core", oper: 2, admin: 1));

        Assert.Equal(AlertSeverity.Critical, observation.Severity);
        Assert.Equal($"check:{check.Id}:if:1:oper-status", observation.RuleId);
        Assert.Equal("interface.1.oper_status", observation.MetricName);
        // The alias first, because the person reading the ticket at three in the morning wants to
        // know what the cable goes to before they want to know which port it is in.
        Assert.Equal("Gi0/2 (uplink to core) on 10.0.0.1 is down.", observation.Summary);
    }

    /// <summary>
    /// A switch ships with every unused port down. Alerting on those would bury the one uplink that
    /// matters under forty-seven ports nobody has patched.
    /// </summary>
    [Fact]
    public void Observe_AnInterfaceSomebodyShut_IsOkRatherThanAFault()
    {
        var observation = Single(Check(), Interface(1, "Gi0/9", oper: 2, admin: 2));

        Assert.Equal(AlertSeverity.Ok, observation.Severity);
    }

    [Fact]
    public void Observe_AnInterfaceWhoseLowerLayerIsDown_IsCritical() =>
        Assert.Equal(
            AlertSeverity.Critical,
            Single(Check(), Interface(1, "Gi0/1", oper: 7, admin: 1)).Severity);

    /// <summary>
    /// An agent that reports no ifAdminStatus is read as meaning the port to be up: the alternative
    /// is a device whose interfaces can never alert at all.
    /// </summary>
    [Fact]
    public void Observe_AnInterfaceDownOnADeviceThatReportsNoAdminStatus_IsStillCritical() =>
        Assert.Equal(
            AlertSeverity.Critical,
            Single(Check(), Interface(1, "Gi0/1", oper: 2, admin: null)).Severity);

    /// <summary>A vendor's private status is neither up nor down, and must not be reported as either.</summary>
    [Fact]
    public void Observe_AStatusTheMibDoesNotDefine_IsOk()
    {
        var observation = Single(Check(), Interface(1, "Gi0/1", oper: 99, admin: 1));

        Assert.Equal(AlertSeverity.Ok, observation.Severity);
        Assert.Contains("unknown state", observation.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Observe_AnInterfaceThatCameBackUp_ReadsAsUp() =>
        Assert.Equal(
            "Gi0/1 on 10.0.0.1 is up.",
            Single(Check(), Interface(1, "Gi0/1", oper: 1, admin: 1)).Summary);

    /// <summary>
    /// The property that keeps one dead switch from being forty-eight alerts: a failed check carries
    /// no samples, so its ports say nothing and its availability rule — which is not this class's —
    /// is what fires. An interface is only ever reported down by a device that is up enough to say so.
    /// </summary>
    [Fact]
    public void Observe_AFailedCheck_ProducesNoInterfaceObservationsAtAll()
    {
        var check = Check();
        var result = Result(check, succeeded: false, Interface(1, "Gi0/1", oper: 1, admin: 1));

        Assert.Empty(InterfaceAlertRules.Observe(result, check, Policy, State()));
        Assert.Empty(InterfaceAlertRules.RuleIds(result, check));
    }

    [Fact]
    public void Observe_ACheckThatReportsNoInterfaces_HasNoInterfaceRules()
    {
        var check = Check();
        var result = new DeviceCheckResult(
            Guid.CreateVersion7(), Guid.CreateVersion7(), check.Id, "Snmp", check.Name,
            "10.0.0.1", Now, Succeeded: true, LatencyMs: 4, Error: null,
            Metrics: [new MetricSample("cpu.utilisation_percent", 12, null, "%")]);

        Assert.Empty(InterfaceAlertRules.Observe(result, check, Policy, State()));
    }

    // ---- utilisation ----

    [Fact]
    public void Observe_AnInterfaceOverItsCriticalThreshold_IsCriticalAndNamesTheThreshold()
    {
        var check = Check(warning: 70, critical: 90);
        var observation = Observations(check, Interface(1, "Gi0/1", oper: 1, admin: 1, utilisation: 94.2))
            .Single(candidate => candidate.RuleId.EndsWith(":utilisation", StringComparison.Ordinal));

        Assert.Equal(AlertSeverity.Critical, observation.Severity);
        Assert.Equal("interface.1.utilisation_percent", observation.MetricName);
        Assert.Equal(94.2, observation.Value);
        Assert.Equal(90, observation.Threshold);
        Assert.Equal(
            "Gi0/1 on 10.0.0.1 is 94.2% utilised, at or above the critical threshold of 90%.",
            observation.Summary);
    }

    /// <summary>
    /// The check's thresholds are consumed here, per port — which is why
    /// <see cref="AlertRules.PrimaryMetric"/> gives an interface check no rule of its own. Both
    /// halves of that arrangement have to hold, or a busy port either alerts twice or not at all.
    /// </summary>
    [Fact]
    public void RuleIds_ForAnInterfacesCheck_AreOnePairPerInterfaceAndNoCheckWideThresholdRule()
    {
        var check = Check(warning: 70, critical: 90);
        var result = Result(
            check,
            succeeded: true,
            Interface(1, "Gi0/1", oper: 1, admin: 1, utilisation: 12),
            Interface(2, "Gi0/2", oper: 1, admin: 1, utilisation: 80));

        Assert.Equal(
            [
                $"check:{check.Id}:if:1:oper-status",
                $"check:{check.Id}:if:1:utilisation",
                $"check:{check.Id}:if:2:oper-status",
                $"check:{check.Id}:if:2:utilisation",
            ],
            InterfaceAlertRules.RuleIds(result, check));

        Assert.Null(AlertRules.RuleIds(check).Threshold);
    }

    /// <summary>An interface check with no thresholds still watches every port for going down.</summary>
    [Fact]
    public void RuleIds_ForACheckWithNoThresholds_AreTheOperStatusRulesOnly()
    {
        var check = Check();
        var result = Result(check, succeeded: true, Interface(1, "Gi0/1", oper: 1, admin: 1, utilisation: 99));

        Assert.Equal([$"check:{check.Id}:if:1:oper-status"], InterfaceAlertRules.RuleIds(result, check));
    }

    /// <summary>
    /// Hysteresis reads a value more leniently while the rule is already alerting, which is why the
    /// engine loads each rule's state before anything is judged. A port sitting on its threshold
    /// would otherwise flap every cycle.
    /// </summary>
    [Fact]
    public void Observe_AnInterfaceAlreadyCritical_StaysCriticalInsideTheHysteresisMargin()
    {
        var check = Check(warning: 70, critical: 90);
        var ruleId = InterfaceAlertRules.RuleId(check.Id, 1, InterfaceAlertRules.UtilisationRule);
        var state = new Dictionary<string, AlertState>(StringComparer.Ordinal)
        {
            [ruleId] = new AlertState { Severity = AlertSeverity.Critical },
        };

        var observation = InterfaceAlertRules
            .Observe(
                Result(check, succeeded: true, Interface(1, "Gi0/1", oper: 1, admin: 1, utilisation: 87)),
                check, Policy, state)
            .Single(candidate => candidate.RuleId == ruleId);

        // 87% is below the 90 it breached at, but inside the 5% margin, so the alert holds.
        Assert.Equal(AlertSeverity.Critical, observation.Severity);
    }

    // ---- helpers ----

    private static AlertObservation Single(CheckDefinition check, IReadOnlyList<MetricSample> samples) =>
        Assert.Single(Observations(check, samples));

    private static IReadOnlyList<AlertObservation> Observations(
        CheckDefinition check,
        params IReadOnlyList<MetricSample>[] interfaces) =>
        InterfaceAlertRules.Observe(Result(check, succeeded: true, interfaces), check, Policy, State());

    private static Dictionary<string, AlertState> State() => new(StringComparer.Ordinal);

    private static IReadOnlyList<MetricSample> Interface(
        int ifIndex,
        string name,
        double? oper,
        double? admin,
        string? alias = null,
        double? utilisation = null)
    {
        var samples = new List<MetricSample> { new($"interface.{ifIndex}.name", null, name, null) };
        if (alias is not null) samples.Add(new($"interface.{ifIndex}.alias", null, alias, null));
        if (oper is not null) samples.Add(new($"interface.{ifIndex}.oper_status", oper, null, null));
        if (admin is not null) samples.Add(new($"interface.{ifIndex}.admin_status", admin, null, null));
        if (utilisation is not null)
        {
            samples.Add(new($"interface.{ifIndex}.utilisation_percent", utilisation, null, "%"));
        }

        return samples;
    }

    private static DeviceCheckResult Result(
        CheckDefinition check,
        bool succeeded,
        params IReadOnlyList<MetricSample>[] interfaces) => new(
        Guid.CreateVersion7(),
        Guid.CreateVersion7(),
        check.Id,
        "Snmp",
        check.Name,
        "10.0.0.1",
        Now,
        succeeded,
        LatencyMs: succeeded ? 12 : null,
        Error: succeeded ? null : "Timed out after 5s",
        Metrics: succeeded ? [.. interfaces.SelectMany(samples => samples)] : []);

    private static CheckDefinition Check(double? warning = null, double? critical = null) => new()
    {
        Id = Guid.CreateVersion7(),
        Name = "SNMP: interfaces",
        Type = CheckType.Snmp,
        IntervalSeconds = 60,
        TimeoutSeconds = 5,
        WarningThreshold = warning,
        CriticalThreshold = critical,
        Comparison = ThresholdComparison.GreaterThan,
        ParametersJson = """{"metric":"interfaces"}""",
        CreatedBy = "test",
        UpdatedBy = "test",
    };
}
