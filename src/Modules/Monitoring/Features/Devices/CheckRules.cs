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

    public const int MinimumPort = 1;
    public const int MaximumPort = 65_535;

    /// <summary>The HTTP methods a check may use. A check reads a service; it never changes one.</summary>
    public static IReadOnlyList<string> HttpMethods { get; } = ["GET", "HEAD"];

    /// <summary>The parameter each check type cannot run without. ICMP needs only the device address.</summary>
    public static IReadOnlyDictionary<CheckType, string> RequiredParameter { get; } =
        new Dictionary<CheckType, string>
        {
            [CheckType.Snmp] = "oid",
            [CheckType.Tcp] = "port",
            [CheckType.Http] = "url",
            // Not defaulted to 443: a TLS check exists to be pointed at a specific listener, and a
            // silent default would make a check against the wrong port look like a certificate fault.
            [CheckType.Tls] = "port",
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

        if (ParameterProblem(type, parameters) is { } problem)
        {
            errors["Parameters"] = [problem];
        }

        return errors;
    }

    /// <summary>
    /// The first thing wrong with a check's per-type parameters, or null if there is nothing.
    /// <para>
    /// One problem rather than all of them, because the parameters are one field on the form and a
    /// list of complaints about a dictionary is harder to act on than the first thing to fix.
    /// </para>
    /// </summary>
    private static string? ParameterProblem(
        CheckType type,
        IReadOnlyDictionary<string, string> parameters)
    {
        if (RequiredParameter.TryGetValue(type, out var required)
            && (!parameters.TryGetValue(required, out var supplied) || string.IsNullOrWhiteSpace(supplied)))
        {
            return $"A {type} check requires a '{required}' parameter.";
        }

        return type switch
        {
            CheckType.Tcp => PortProblem(parameters, "TCP"),
            CheckType.Tls => PortProblem(parameters, "TLS"),
            CheckType.Http => HttpProblem(parameters),
            _ => null,
        };
    }

    private static string? PortProblem(IReadOnlyDictionary<string, string> parameters, string what) =>
        parameters.TryGetValue("port", out var port)
        && (!int.TryParse(port, out var number) || number < MinimumPort || number > MaximumPort)
            ? $"'port' must be a {what} port between {MinimumPort} and {MaximumPort}."
            : null;

    private static string? HttpProblem(IReadOnlyDictionary<string, string> parameters)
    {
        if (parameters.TryGetValue("url", out var url)
            && (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)
                || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)))
        {
            return "'url' must be an absolute http or https URL.";
        }

        // One code rather than a list or a `2xx` class. The poller has to agree with this rule
        // exactly, in another language, and every form the rule can take is a form the two can
        // disagree about — the same trap `AlertRules.PrimaryMetric` already carries. Omitted means
        // "any 2xx", which is what a service check means by "the site is up".
        if (Supplied(parameters, "expectedStatus") is { } status
            && (!int.TryParse(status, out var code) || code is < 100 or > 599))
        {
            return "'expectedStatus' must be an HTTP status code between 100 and 599.";
        }

        var method = Supplied(parameters, "method");
        if (method is not null
            && !HttpMethods.Contains(method, StringComparer.OrdinalIgnoreCase))
        {
            return $"'method' must be one of {string.Join(", ", HttpMethods)}.";
        }

        // A HEAD response has no body, so a content expectation against one can never be met — the
        // check would fail forever against a service that is answering perfectly well.
        if (Supplied(parameters, "expectedContent") is not null
            && string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase))
        {
            return "'expectedContent' cannot be matched against a HEAD request, which has no body.";
        }

        return null;
    }

    /// <summary>A parameter an operator actually set. A blank field is an unset one, not an empty value.</summary>
    private static string? Supplied(IReadOnlyDictionary<string, string> parameters, string name) =>
        parameters.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : null;

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
