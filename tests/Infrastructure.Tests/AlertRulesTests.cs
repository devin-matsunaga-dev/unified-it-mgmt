using Contracts.Events;

using Modules.Monitoring.Data;
using Modules.Monitoring.Features.Alerting;

namespace Infrastructure.Tests;

/// <summary>
/// Which rules a check result feeds, and with what. This is where "a failed check is a fact" and "a
/// check has one number its thresholds are about" are asserted — both easy to get wrong in a way no
/// integration test would notice, because the wrong rule still produces alerts.
/// </summary>
public sealed class AlertRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 10, 9, 0, 0, TimeSpan.Zero);

    private static readonly AlertPolicy Policy = new(3, 2, 5, 4, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10));

    // ---- which metric a check's thresholds are about ----

    [Theory]
    [InlineData(CheckType.Icmp, null, "icmp.rtt_ms")]
    [InlineData(CheckType.Snmp, "cpu", "cpu.utilisation_percent")]
    [InlineData(CheckType.Snmp, "memory", "memory.used_percent")]
    [InlineData(CheckType.Tcp, null, "check.latency_ms")]
    [InlineData(CheckType.Http, null, "check.latency_ms")]
    // A TLS check is about the certificate rather than the handshake, so unlike its two neighbours
    // it does not take the latency every check reports.
    [InlineData(CheckType.Tls, null, "tls.days_to_expiry")]
    public void PrimaryMetric_ForAKnownFamily_IsTheOneTheCheckIsAbout(
        CheckType type,
        string? family,
        string expected)
    {
        var parameters = family is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal) { ["metric"] = family };

        Assert.Equal(expected, AlertRules.PrimaryMetric(type, parameters));
    }

    /// <summary>
    /// An SNMP system-information check reports names and descriptions and no numbers at all, so it
    /// has nothing a threshold could be about — however it is configured.
    /// </summary>
    [Fact]
    public void PrimaryMetric_ForSysInfo_IsNothing() =>
        Assert.Null(AlertRules.PrimaryMetric(
            CheckType.Snmp,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["metric"] = "sysinfo" }));

    /// <summary>SNMP with no family named is sysinfo, which is the poller's own default.</summary>
    [Fact]
    public void PrimaryMetric_ForSnmpWithNoFamily_IsNothing() =>
        Assert.Null(AlertRules.PrimaryMetric(
            CheckType.Snmp, new Dictionary<string, string>(StringComparer.Ordinal)));

    /// <summary>
    /// A raw OID is named by whoever configured it, and the fallback is the OID itself — the same
    /// rule the poller applies, so the two cannot disagree about what the metric is called.
    /// </summary>
    [Fact]
    public void PrimaryMetric_ForARawOid_FollowsThePollersNamingRule()
    {
        Assert.Equal("psu.watts", AlertRules.PrimaryMetric(
            CheckType.Snmp,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["metric"] = "oid",
                ["oid"] = "1.3.6.1.4.1.9.1.1",
                ["metricName"] = "psu.watts",
            }));

        Assert.Equal("snmp.1.3.6.1.4.1.9.1.1", AlertRules.PrimaryMetric(
            CheckType.Snmp,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["metric"] = "oid",
                ["oid"] = "1.3.6.1.4.1.9.1.1",
            }));
    }

    // ---- which rules a check has ----

    [Fact]
    public void RuleIds_ForACheckWithNoThresholds_HasAvailabilityOnly()
    {
        var check = Check(warning: null, critical: null);
        var rules = AlertRules.RuleIds(check);

        Assert.Equal($"check:{check.Id}:availability", rules.Availability);
        Assert.Null(rules.Threshold);
        Assert.Null(rules.MetricName);
    }

    [Fact]
    public void RuleIds_ForAThresholdedCheck_HasBoth()
    {
        var check = Check(warning: 70, critical: 90);
        var rules = AlertRules.RuleIds(check);

        Assert.Equal($"check:{check.Id}:availability", rules.Availability);
        Assert.Equal($"check:{check.Id}:cpu.utilisation_percent", rules.Threshold);
        Assert.Equal("cpu.utilisation_percent", rules.MetricName);
    }

    /// <summary>
    /// Rule ids have to survive a restart and be identical on a recurrence, because WP-3.6 dedupes
    /// tickets on <c>alert:{deviceId}:{ruleId}</c>. Derived from the check id, so they do.
    /// </summary>
    [Fact]
    public void RuleIds_ForTheSameCheck_AreStableAcrossCalls()
    {
        var check = Check(70, 90);

        Assert.Equal(AlertRules.RuleIds(check).Threshold, AlertRules.RuleIds(check).Threshold);
        Assert.Equal(AlertRules.RuleIds(check).Availability, AlertRules.RuleIds(check).Availability);
    }

    // ---- what a result observes ----

    [Fact]
    public void Observe_ASucceedingCheck_ReportsAvailabilityOkAndTheReading()
    {
        var check = Check(70, 90);
        var observations = Observe(check, Result(check, succeeded: true, cpu: 42));

        Assert.Equal(2, observations.Count);

        var availability = observations[0];
        Assert.Equal("check.success", availability.MetricName);
        Assert.Equal(AlertSeverity.Ok, availability.Severity);
        Assert.Equal(1d, availability.Value);

        var threshold = observations[1];
        Assert.Equal("cpu.utilisation_percent", threshold.MetricName);
        Assert.Equal(AlertSeverity.Ok, threshold.Severity);
        Assert.Equal(42d, threshold.Value);
    }

    [Fact]
    public void Observe_AReadingPastCritical_ReportsCriticalWithTheConfiguredThreshold()
    {
        var check = Check(70, 90);
        var observations = Observe(check, Result(check, succeeded: true, cpu: 95));

        var threshold = observations[1];
        Assert.Equal(AlertSeverity.Critical, threshold.Severity);
        Assert.Equal(90d, threshold.Threshold);
        Assert.Contains("critical threshold of 90", threshold.Summary, StringComparison.Ordinal);
    }

    /// <summary>
    /// The rule that makes an unreachable device an alert rather than a gap: a failed check reports,
    /// and it reports through availability because there is no number to compare.
    /// </summary>
    [Fact]
    public void Observe_AFailedCheck_ReportsAvailabilityCriticalAndNoThresholdReading()
    {
        var check = Check(70, 90);
        var observations = Observe(check, Result(check, succeeded: false, cpu: null));

        var availability = Assert.Single(observations);
        Assert.Equal(AlertSeverity.Critical, availability.Severity);
        Assert.Equal(0d, availability.Value);
        Assert.Contains("Timed out", availability.Summary, StringComparison.Ordinal);
    }

    /// <summary>
    /// The poller writes whole sentences, so the summary must not add a second full stop to one. Every
    /// failing check in the live estate read "…after 3 packets.." before this.
    /// </summary>
    [Theory]
    [InlineData("No reply from 192.0.2.1 after 3 packets.", "packets.")]
    [InlineData("TCP connect to mailhog:1026 failed: [Errno 111] Connect call failed.", "failed.")]
    [InlineData("The agent stopped answering!", "answering!")]
    [InlineData("Is the listener up?", "up?")]
    public void Observe_AFailureReasonThatIsAlreadyASentence_IsNotGivenASecondFullStop(
        string error,
        string expectedEnding)
    {
        var check = Check(70, 90);

        var availability = Assert.Single(Observe(check, Result(check, error)));

        Assert.EndsWith(expectedEnding, availability.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("..", availability.Summary, StringComparison.Ordinal);
    }

    /// <summary>A reason that stops mid-air still gets terminated, or it runs into whatever follows.</summary>
    [Theory]
    [InlineData("Timed out after 5s", "Timed out after 5s.")]
    [InlineData("Timed out after 5s   ", "Timed out after 5s.")]
    public void Observe_AFailureReasonWithNoPunctuation_IsTerminated(string error, string expected)
    {
        var check = Check(70, 90);

        var availability = Assert.Single(Observe(check, Result(check, error)));

        Assert.EndsWith(expected, availability.Summary, StringComparison.Ordinal);
    }

    /// <summary>
    /// Nobody measured the CPU of a switch that did not answer. Treating the absence as a good reading
    /// would clear a threshold alert at exactly the moment the device disappeared.
    /// </summary>
    [Fact]
    public void Observe_AFailedCheck_DoesNotAdvanceTheThresholdRule()
    {
        var check = Check(70, 90);
        var observations = Observe(check, Result(check, succeeded: false, cpu: null));

        Assert.DoesNotContain(observations, observation => observation.MetricName == "cpu.utilisation_percent");
    }

    /// <summary>
    /// An SNMP CPU read reports a figure per core beside the average. Alerting on each of them would
    /// turn one busy host into nine alerts, so only the metric the check is about is judged.
    /// </summary>
    [Fact]
    public void Observe_ACheckReportingSeveralMetrics_JudgesOnlyItsPrimaryOne()
    {
        var check = Check(70, 90);
        var result = new DeviceCheckResult(
            Guid.CreateVersion7(), Guid.CreateVersion7(), check.Id, "Snmp", check.Name, "10.0.0.1",
            Now, Succeeded: true, LatencyMs: 4, Error: null,
            Metrics:
            [
                new MetricSample("cpu.core_1_percent", 99, null, "%"),
                new MetricSample("cpu.core_2_percent", 98, null, "%"),
                new MetricSample("cpu.utilisation_percent", 30, null, "%"),
            ]);

        var observations = Observe(check, result);

        Assert.Equal(2, observations.Count);
        Assert.Equal(30d, observations[1].Value);
        Assert.Equal(AlertSeverity.Ok, observations[1].Severity);
    }

    /// <summary>
    /// Configured with a threshold on a number this run did not report. Not an error and not an
    /// alert — the check succeeded, it just did not produce that metric this cycle.
    /// </summary>
    [Fact]
    public void Observe_WhenThePrimaryMetricIsAbsent_ReportsAvailabilityOnly()
    {
        var check = Check(70, 90);
        var result = new DeviceCheckResult(
            Guid.CreateVersion7(), Guid.CreateVersion7(), check.Id, "Snmp", check.Name, "10.0.0.1",
            Now, Succeeded: true, LatencyMs: 4, Error: null,
            Metrics: [new MetricSample("memory.used_percent", 12, null, "%")]);

        Assert.Single(Observe(check, result));
    }

    /// <summary>
    /// The state it is read with is what makes hysteresis work. A reading of 68 is Ok from Ok and
    /// still Warning from Warning, and this is the only place that distinction enters the pipeline.
    /// </summary>
    [Fact]
    public void Observe_ReadsTheValueAgainstTheRulesCurrentState()
    {
        var check = Check(70, 90);
        var result = Result(check, succeeded: true, cpu: 68);
        var rules = AlertRules.RuleIds(check);

        var fromOk = AlertRules.Observe(result, check, rules, Policy,
            new Dictionary<string, AlertState>(StringComparer.Ordinal));
        Assert.Equal(AlertSeverity.Ok, fromOk[1].Severity);

        var fromWarning = AlertRules.Observe(result, check, rules, Policy,
            new Dictionary<string, AlertState>(StringComparer.Ordinal)
            {
                [rules.Threshold!] = new AlertState(Severity: AlertSeverity.Warning),
            });
        Assert.Equal(AlertSeverity.Warning, fromWarning[1].Severity);
    }

    /// <summary>
    /// One malformed parameters document must not stop a device being evaluated. The check loses its
    /// threshold rule and keeps the availability rule, which is the more important of the two.
    /// </summary>
    [Fact]
    public void RuleIds_ForACheckWithUnreadableParameters_KeepsAvailability()
    {
        var check = Check(70, 90);
        check.ParametersJson = "{ this is not json";

        var rules = AlertRules.RuleIds(check);

        Assert.Equal($"check:{check.Id}:availability", rules.Availability);
        Assert.Null(rules.Threshold);
    }

    // ---- failure paths ----

    [Fact]
    public void RuleIds_WithNoCheck_Throws() =>
        Assert.Throws<ArgumentNullException>(() => AlertRules.RuleIds(null!));

    [Fact]
    public void Observe_WithNoResult_Throws()
    {
        var check = Check(70, 90);
        Assert.Throws<ArgumentNullException>(() => AlertRules.Observe(
            null!, check, AlertRules.RuleIds(check), Policy,
            new Dictionary<string, AlertState>(StringComparer.Ordinal)));
    }

    // ---- fixtures ----

    private static IReadOnlyList<AlertObservation> Observe(CheckDefinition check, DeviceCheckResult result) =>
        AlertRules.Observe(
            result, check, AlertRules.RuleIds(check), Policy,
            new Dictionary<string, AlertState>(StringComparer.Ordinal));

    /// <summary>A failed check carrying a particular reason, for the summary's wording.</summary>
    private static DeviceCheckResult Result(CheckDefinition check, string error) => new(
        Guid.CreateVersion7(),
        Guid.CreateVersion7(),
        check.Id,
        "Snmp",
        check.Name,
        "10.0.0.1",
        Now,
        Succeeded: false,
        LatencyMs: null,
        Error: error,
        Metrics: []);

    private static DeviceCheckResult Result(CheckDefinition check, bool succeeded, double? cpu) => new(
        Guid.CreateVersion7(),
        Guid.CreateVersion7(),
        check.Id,
        "Snmp",
        check.Name,
        "10.0.0.1",
        Now,
        succeeded,
        LatencyMs: succeeded ? 4 : null,
        Error: succeeded ? null : "Timed out after 5s",
        Metrics: cpu is { } value
            ? [new MetricSample("cpu.utilisation_percent", value, null, "%")]
            : []);

    private static CheckDefinition Check(double? warning, double? critical) => new()
    {
        Id = Guid.CreateVersion7(),
        Name = "SNMP: CPU",
        Type = CheckType.Snmp,
        IntervalSeconds = 60,
        TimeoutSeconds = 5,
        WarningThreshold = warning,
        CriticalThreshold = critical,
        Comparison = ThresholdComparison.GreaterThan,
        ParametersJson = """{"metric":"cpu"}""",
        CreatedBy = "test",
        UpdatedBy = "test",
    };
}
