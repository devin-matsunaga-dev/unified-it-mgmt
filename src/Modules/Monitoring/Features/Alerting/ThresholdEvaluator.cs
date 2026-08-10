using Modules.Monitoring.Data;

namespace Modules.Monitoring.Features.Alerting;

/// <summary>
/// Turns one reading into one severity, with hysteresis. Pure, so the whole matrix — rising and
/// falling comparisons, a missing warning threshold, a value sitting exactly on the line, a negative
/// threshold — is unit-testable without a database, a clock or a device.
/// </summary>
public static class ThresholdEvaluator
{
    /// <summary>
    /// The severity a value implies, judged more leniently the worse the rule already is.
    /// <para>
    /// Hysteresis is one-directional on purpose: getting worse uses the configured threshold, and
    /// getting better uses one relaxed by <see cref="AlertPolicy.HysteresisPercent"/>. Without it a
    /// device parked on its threshold alternates every cycle, and the flap policy — which exists for
    /// devices that genuinely misbehave — would spend its budget on arithmetic.
    /// </para>
    /// </summary>
    /// <param name="current">
    /// What the rule is at now. Only this makes the function asymmetric; passing
    /// <see cref="AlertSeverity.Ok"/> gives the plain threshold comparison.
    /// </param>
    public static AlertSeverity Assess(
        double value,
        double? warningThreshold,
        double? criticalThreshold,
        ThresholdComparison comparison,
        AlertSeverity current,
        double hysteresisPercent)
    {
        if (criticalThreshold is { } critical
            && Breaches(value, Effective(critical, comparison, current >= AlertSeverity.Critical, hysteresisPercent), comparison))
        {
            return AlertSeverity.Critical;
        }

        if (warningThreshold is { } warning
            && Breaches(value, Effective(warning, comparison, current >= AlertSeverity.Warning, hysteresisPercent), comparison))
        {
            return AlertSeverity.Warning;
        }

        return AlertSeverity.Ok;
    }

    /// <summary>Whether the rule has a threshold to judge anything by at all.</summary>
    public static bool HasThreshold(CheckDefinition check)
    {
        ArgumentNullException.ThrowIfNull(check);
        return check.WarningThreshold is not null || check.CriticalThreshold is not null;
    }

    /// <summary>
    /// The threshold that was crossed, for the alert's record: the critical one where it is set,
    /// otherwise the warning one. Reported rather than recomputed so an operator reading an alert
    /// sees the number they configured, not one relaxed by hysteresis.
    /// </summary>
    public static double? CrossedThreshold(CheckDefinition check, AlertSeverity severity)
    {
        ArgumentNullException.ThrowIfNull(check);
        return severity switch
        {
            AlertSeverity.Critical => check.CriticalThreshold ?? check.WarningThreshold,
            AlertSeverity.Warning => check.WarningThreshold ?? check.CriticalThreshold,
            _ => null,
        };
    }

    private static bool Breaches(double value, double threshold, ThresholdComparison comparison) =>
        comparison is ThresholdComparison.GreaterThan ? value >= threshold : value <= threshold;

    /// <summary>
    /// Moves the threshold away from the bad side by the hysteresis margin, but only while the rule
    /// is already at that level. The margin is a percentage of the threshold's magnitude, so it works
    /// for a negative one (a temperature floor) as well as for a percentage.
    /// </summary>
    private static double Effective(
        double threshold,
        ThresholdComparison comparison,
        bool alreadyAtThisLevel,
        double hysteresisPercent)
    {
        if (!alreadyAtThisLevel || hysteresisPercent <= 0)
        {
            return threshold;
        }

        var margin = Math.Abs(threshold) * hysteresisPercent / 100d;
        return comparison is ThresholdComparison.GreaterThan ? threshold - margin : threshold + margin;
    }
}
