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
        IReadOnlyDictionary<string, string> parameters)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

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
}
