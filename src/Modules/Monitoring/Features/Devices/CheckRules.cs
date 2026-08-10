using Modules.Monitoring.Data;

namespace Modules.Monitoring.Features.Devices;

/// <summary>
/// What makes a check definition coherent. These are rules about the relationship between fields — a
/// timeout longer than the interval, a critical threshold on the safe side of the warning — so they
/// live here rather than in a FluentValidation rule set that can only see one property at a time.
/// Pure and infrastructure-free, so the whole matrix is unit-testable.
/// </summary>
public static class CheckRules
{
    public const int MinimumIntervalSeconds = 10;
    public const int MaximumIntervalSeconds = 86_400;
    public const int MinimumTimeoutSeconds = 1;
    public const int MaximumTimeoutSeconds = 300;
    public const int MaximumParameters = 20;

    /// <summary>The parameter each check type cannot run without. ICMP needs only the device address.</summary>
    public static IReadOnlyDictionary<CheckType, string> RequiredParameter { get; } =
        new Dictionary<CheckType, string>
        {
            [CheckType.Snmp] = "oid",
            [CheckType.Tcp] = "port",
            [CheckType.Http] = "url",
        };

    public static IReadOnlyDictionary<string, string[]> Validate(
        CheckType type,
        int intervalSeconds,
        int timeoutSeconds,
        double? warningThreshold,
        double? criticalThreshold,
        ThresholdComparison comparison,
        IReadOnlyDictionary<string, string> parameters,
        AlertTuningRequest? alertTuning = null)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        ValidateAlertTuning(alertTuning, errors);

        if (intervalSeconds is < MinimumIntervalSeconds or > MaximumIntervalSeconds)
        {
            errors["IntervalSeconds"] =
                [$"Interval must be between {MinimumIntervalSeconds} and {MaximumIntervalSeconds} seconds."];
        }

        if (timeoutSeconds is < MinimumTimeoutSeconds or > MaximumTimeoutSeconds)
        {
            errors["TimeoutSeconds"] =
                [$"Timeout must be between {MinimumTimeoutSeconds} and {MaximumTimeoutSeconds} seconds."];
        }
        else if (timeoutSeconds >= intervalSeconds)
        {
            // A check still waiting when the next run is due either overlaps itself or skips a cycle;
            // either way the interval it reports is not the interval it was configured with.
            errors["TimeoutSeconds"] = ["Timeout must be shorter than the interval."];
        }

        if (warningThreshold is { } warning && criticalThreshold is { } critical)
        {
            var ordered = comparison is ThresholdComparison.GreaterThan
                ? critical >= warning
                : critical <= warning;
            if (!ordered)
            {
                var direction = comparison is ThresholdComparison.GreaterThan ? "above" : "below";
                errors["CriticalThreshold"] =
                    [$"Critical must be at or {direction} the warning threshold for a {comparison} check."];
            }
        }

        if (parameters.Count > MaximumParameters)
        {
            errors["Parameters"] = [$"A check carries at most {MaximumParameters} parameters."];
        }

        foreach (var (key, value) in parameters)
        {
            if (string.IsNullOrWhiteSpace(key) || key.Length > 50)
            {
                errors["Parameters"] = ["Parameter names must be 1 to 50 characters."];
                break;
            }

            if (value.Length > 500)
            {
                errors["Parameters"] = [$"Parameter '{key}' is longer than 500 characters."];
                break;
            }
        }

        if (RequiredParameter.TryGetValue(type, out var required)
            && (!parameters.TryGetValue(required, out var suppliedValue) || string.IsNullOrWhiteSpace(suppliedValue)))
        {
            errors["Parameters"] = [$"A {type} check requires a '{required}' parameter."];
        }
        else if (type is CheckType.Tcp
            && parameters.TryGetValue("port", out var port)
            && (!int.TryParse(port, out var portNumber) || portNumber is < 1 or > 65_535))
        {
            errors["Parameters"] = ["'port' must be a TCP port between 1 and 65535."];
        }
        else if (type is CheckType.Http
            && parameters.TryGetValue("url", out var url)
            && (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)
                || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)))
        {
            errors["Parameters"] = ["'url' must be an absolute http or https URL."];
        }

        return errors;
    }

    public const int MaximumSustainedCycles = 100;
    public const int MaximumFlapWindowSeconds = 86_400;

    /// <summary>
    /// Bounds on WP-3.5's per-check alert tuning. Each is a count of cycles or a span of time and each
    /// is optional, so the only thing to check is that a supplied value is one somebody could have
    /// meant: a sustain count of zero alerts on the first dropped packet, and one of ten thousand
    /// silently disables the check's rules without saying so anywhere.
    /// </summary>
    private static void ValidateAlertTuning(
        AlertTuningRequest? tuning,
        Dictionary<string, string[]> errors)
    {
        if (tuning is null)
        {
            return;
        }

        if (tuning.SustainedCycles is { } sustained and (< 1 or > MaximumSustainedCycles))
        {
            errors["AlertTuning.SustainedCycles"] =
                [$"Sustained cycles must be between 1 and {MaximumSustainedCycles}; got {sustained}."];
        }

        if (tuning.RecoveryCycles is { } recovery and (< 1 or > MaximumSustainedCycles))
        {
            errors["AlertTuning.RecoveryCycles"] =
                [$"Recovery cycles must be between 1 and {MaximumSustainedCycles}; got {recovery}."];
        }

        // At 100% the relaxed threshold reaches zero and a rule can never recover from a rising
        // comparison, which is a silent way to make an alert permanent.
        if (tuning.HysteresisPercent is { } hysteresis and (< 0 or >= 100))
        {
            errors["AlertTuning.HysteresisPercent"] =
                ["Hysteresis must be at least 0 and below 100 percent."];
        }

        if (tuning.FlapThreshold is { } flaps and < 2)
        {
            errors["AlertTuning.FlapThreshold"] =
                ["Flap threshold must be at least 2 — a single state change is not a flap."];
        }

        if (tuning.FlapWindowSeconds is { } window and (< 1 or > MaximumFlapWindowSeconds))
        {
            errors["AlertTuning.FlapWindowSeconds"] =
                [$"The flap window must be between 1 and {MaximumFlapWindowSeconds} seconds."];
        }
    }
}
