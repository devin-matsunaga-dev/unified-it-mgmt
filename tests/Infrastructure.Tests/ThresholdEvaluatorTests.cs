using Modules.Monitoring.Data;
using Modules.Monitoring.Features.Alerting;

namespace Infrastructure.Tests;

/// <summary>
/// Reading a number against two thresholds, in both directions, with and without hysteresis. Pure —
/// the whole matrix without a device.
/// </summary>
public sealed class ThresholdEvaluatorTests
{
    // ---- a rising metric: CPU, latency, temperature ----

    [Theory]
    [InlineData(10, AlertSeverity.Ok)]
    [InlineData(69.9, AlertSeverity.Ok)]
    [InlineData(70, AlertSeverity.Warning)]
    [InlineData(85, AlertSeverity.Warning)]
    [InlineData(90, AlertSeverity.Critical)]
    [InlineData(99, AlertSeverity.Critical)]
    public void Assess_RisingMetricFromOk_UsesTheConfiguredThresholds(double value, AlertSeverity expected) =>
        Assert.Equal(expected, ThresholdEvaluator.Assess(
            value, 70, 90, ThresholdComparison.GreaterThan, AlertSeverity.Ok, hysteresisPercent: 5));

    // ---- a falling metric: free disk, certificate days remaining ----

    [Theory]
    [InlineData(50, AlertSeverity.Ok)]
    [InlineData(20.1, AlertSeverity.Ok)]
    [InlineData(20, AlertSeverity.Warning)]
    [InlineData(11, AlertSeverity.Warning)]
    [InlineData(10, AlertSeverity.Critical)]
    [InlineData(0, AlertSeverity.Critical)]
    public void Assess_FallingMetricFromOk_UsesTheConfiguredThresholds(double value, AlertSeverity expected) =>
        Assert.Equal(expected, ThresholdEvaluator.Assess(
            value, 20, 10, ThresholdComparison.LessThan, AlertSeverity.Ok, hysteresisPercent: 5));

    // ---- hysteresis ----

    /// <summary>
    /// The reason hysteresis exists: a device parked on its threshold would otherwise alternate every
    /// cycle and spend the flap budget on arithmetic. At 5% of 70 the rule stays Warning down to 66.5.
    /// </summary>
    [Theory]
    [InlineData(70)]
    [InlineData(68)]
    [InlineData(66.5)]
    public void Assess_RisingMetricAlreadyWarning_StaysWarningWithinTheMargin(double value) =>
        Assert.Equal(AlertSeverity.Warning, ThresholdEvaluator.Assess(
            value, 70, 90, ThresholdComparison.GreaterThan, AlertSeverity.Warning, hysteresisPercent: 5));

    [Fact]
    public void Assess_RisingMetricAlreadyWarning_RecoversOnceItClearsTheMargin() =>
        Assert.Equal(AlertSeverity.Ok, ThresholdEvaluator.Assess(
            66.4, 70, 90, ThresholdComparison.GreaterThan, AlertSeverity.Warning, hysteresisPercent: 5));

    /// <summary>A critical rule de-escalates to Warning, not straight to Ok, when it half recovers.</summary>
    [Fact]
    public void Assess_RisingMetricAlreadyCritical_DeEscalatesToWarningBelowTheRelaxedCritical() =>
        Assert.Equal(AlertSeverity.Warning, ThresholdEvaluator.Assess(
            85, 70, 90, ThresholdComparison.GreaterThan, AlertSeverity.Critical, hysteresisPercent: 5));

    [Fact]
    public void Assess_FallingMetricAlreadyWarning_StaysWarningWithinTheMargin() =>
        Assert.Equal(AlertSeverity.Warning, ThresholdEvaluator.Assess(
            21, 20, 10, ThresholdComparison.LessThan, AlertSeverity.Warning, hysteresisPercent: 5));

    /// <summary>The margin is a share of the threshold's magnitude, so a floor below zero still works.</summary>
    [Fact]
    public void Assess_NegativeThreshold_RelaxesAwayFromTheBadSide()
    {
        // A temperature floor of -10: already Warning, so it stays Warning until it climbs past -9.5.
        Assert.Equal(AlertSeverity.Warning, ThresholdEvaluator.Assess(
            -9.7, -10, null, ThresholdComparison.LessThan, AlertSeverity.Warning, hysteresisPercent: 5));
        Assert.Equal(AlertSeverity.Ok, ThresholdEvaluator.Assess(
            -9.4, -10, null, ThresholdComparison.LessThan, AlertSeverity.Warning, hysteresisPercent: 5));
    }

    [Fact]
    public void Assess_WithHysteresisDisabled_IsSymmetric() =>
        Assert.Equal(AlertSeverity.Ok, ThresholdEvaluator.Assess(
            69.9, 70, 90, ThresholdComparison.GreaterThan, AlertSeverity.Warning, hysteresisPercent: 0));

    // ---- partly configured checks ----

    [Fact]
    public void Assess_WithOnlyACriticalThreshold_NeverReportsWarning()
    {
        Assert.Equal(AlertSeverity.Ok, ThresholdEvaluator.Assess(
            89, null, 90, ThresholdComparison.GreaterThan, AlertSeverity.Ok, 5));
        Assert.Equal(AlertSeverity.Critical, ThresholdEvaluator.Assess(
            90, null, 90, ThresholdComparison.GreaterThan, AlertSeverity.Ok, 5));
    }

    [Fact]
    public void Assess_WithNoThresholdsAtAll_IsAlwaysOk() =>
        Assert.Equal(AlertSeverity.Ok, ThresholdEvaluator.Assess(
            9_999, null, null, ThresholdComparison.GreaterThan, AlertSeverity.Critical, 5));

    [Fact]
    public void HasThreshold_ForACheckWithNeither_IsFalse() =>
        Assert.False(ThresholdEvaluator.HasThreshold(Check(null, null)));

    [Fact]
    public void HasThreshold_ForACheckWithOnlyAWarning_IsTrue() =>
        Assert.True(ThresholdEvaluator.HasThreshold(Check(70, null)));

    /// <summary>
    /// The number reported on the alert is the one the operator configured, never the relaxed one —
    /// an alert that says "above 66.5" when the form says 70 is an alert nobody can reconcile.
    /// </summary>
    [Fact]
    public void CrossedThreshold_ReportsTheConfiguredValueForTheSeverity()
    {
        var check = Check(70, 90);

        Assert.Equal(90, ThresholdEvaluator.CrossedThreshold(check, AlertSeverity.Critical));
        Assert.Equal(70, ThresholdEvaluator.CrossedThreshold(check, AlertSeverity.Warning));
        Assert.Null(ThresholdEvaluator.CrossedThreshold(check, AlertSeverity.Ok));
    }

    /// <summary>A critical-only check reports its critical value when it raises at Warning, not null.</summary>
    [Fact]
    public void CrossedThreshold_WithOnlyOneConfigured_FallsBackToIt() =>
        Assert.Equal(90, ThresholdEvaluator.CrossedThreshold(Check(null, 90), AlertSeverity.Warning));

    // ---- failure paths ----

    [Fact]
    public void HasThreshold_WithNoCheck_Throws() =>
        Assert.Throws<ArgumentNullException>(() => ThresholdEvaluator.HasThreshold(null!));

    [Fact]
    public void CrossedThreshold_WithNoCheck_Throws() =>
        Assert.Throws<ArgumentNullException>(() =>
            ThresholdEvaluator.CrossedThreshold(null!, AlertSeverity.Critical));

    private static CheckDefinition Check(double? warning, double? critical) => new()
    {
        Id = Guid.CreateVersion7(),
        Name = "CPU",
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
