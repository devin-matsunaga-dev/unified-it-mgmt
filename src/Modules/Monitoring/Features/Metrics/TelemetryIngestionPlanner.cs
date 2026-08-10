using Contracts.Events;

using Modules.Monitoring.Data;

namespace Modules.Monitoring.Features.Metrics;

/// <summary>
/// Turns one telemetry batch into the rows it becomes. Pure, so every rule about what is stored,
/// what is derived and what is refused is testable without a database or a broker — the
/// <c>PollerConfigDeltaPlanner</c> and <c>CheckRules</c> precedent.
/// </summary>
public static class TelemetryIngestionPlanner
{
    /// <summary>
    /// Names ingestion derives from the check result itself. A poller-supplied sample using this
    /// prefix is refused rather than allowed to overwrite one, because "did the check succeed" must
    /// mean the same thing on every device.
    /// </summary>
    public const string ReservedPrefix = "check.";

    /// <summary>Whether the check completed, as a 0/1 series. Averaged over a bucket it is availability.</summary>
    public const string SuccessMetric = "check.success";

    /// <summary>Round-trip time, for the checks that measure one.</summary>
    public const string LatencyMetric = "check.latency_ms";

    private const int MaxMetricNameLength = 100;
    private const int MaxUnitLength = 20;
    private const int MaxFactNameLength = 100;
    private const int MaxFactValueLength = 1_000;

    public static TelemetryIngestionPlan Plan(DeviceTelemetryReported telemetry)
    {
        ArgumentNullException.ThrowIfNull(telemetry);

        var metrics = new List<DeviceMetric>();
        var facts = new Dictionary<(Guid DeviceId, string Name), DeviceInventoryFact>();
        var rejected = new List<string>();
        var pollerName = telemetry.PollerName;

        foreach (var result in telemetry.Results)
        {
            // Derived first, and unconditionally: a failed check produces no samples of its own, so
            // this is the only record that it ran at all. Dropping it would make an unreachable
            // device indistinguishable from one nobody polls.
            metrics.Add(NewMetric(result, SuccessMetric, result.Succeeded ? 1d : 0d, unit: null, pollerName));
            if (result.LatencyMs is { } latency && double.IsFinite(latency))
            {
                metrics.Add(NewMetric(result, LatencyMetric, latency, "ms", pollerName));
            }

            foreach (var sample in result.Metrics)
            {
                var name = sample.Name?.Trim();
                if (string.IsNullOrEmpty(name) || name.Length > MaxMetricNameLength)
                {
                    rejected.Add($"{result.CheckName}: metric name '{sample.Name}' is empty or too long.");
                    continue;
                }

                if (name.StartsWith(ReservedPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    rejected.Add($"{result.CheckName}: metric name '{name}' uses the reserved '{ReservedPrefix}' prefix.");
                    continue;
                }

                // Exactly one of the two is populated by contract. A sample carrying both, or
                // neither, is a poller bug and is refused rather than half-stored.
                var hasValue = sample.Value is { } candidate && double.IsFinite(candidate);
                var hasText = !string.IsNullOrWhiteSpace(sample.Text);
                if (hasValue == hasText)
                {
                    rejected.Add($"{result.CheckName}: metric '{name}' must carry either a value or text.");
                    continue;
                }

                if (hasValue)
                {
                    metrics.Add(NewMetric(result, name, sample.Value!.Value, Truncate(sample.Unit, MaxUnitLength), pollerName));
                    continue;
                }

                if (name.Length > MaxFactNameLength)
                {
                    rejected.Add($"{result.CheckName}: inventory fact '{name}' is too long.");
                    continue;
                }

                // Last writer inside one batch wins, and only forwards: two checks on one device can
                // both report sysName, and a redelivered batch must not overwrite a later reading.
                var key = (result.DeviceId, name);
                if (facts.TryGetValue(key, out var existing) && existing.ObservedAt >= result.ObservedAt)
                {
                    continue;
                }

                facts[key] = new DeviceInventoryFact
                {
                    DeviceId = result.DeviceId,
                    Name = name,
                    Value = Truncate(sample.Text, MaxFactValueLength)!,
                    ObservedAt = result.ObservedAt.ToUniversalTime(),
                };
            }
        }

        return new TelemetryIngestionPlan(metrics, [.. facts.Values], rejected);
    }

    private static DeviceMetric NewMetric(
        DeviceCheckResult result,
        string name,
        double value,
        string? unit,
        string pollerName) =>
        new()
        {
            // Normalised here rather than trusted: Npgsql refuses a DateTimeOffset with a non-zero
            // offset for a timestamptz column, and the offset a poller stamps is its business.
            Time = result.ObservedAt.ToUniversalTime(),
            DeviceId = result.DeviceId,
            CheckId = result.CheckId,
            MetricName = name,
            Value = value,
            Unit = unit,
            CiId = result.CiId,
            PollerName = pollerName,
        };

    private static string? Truncate(string? value, int maxLength) =>
        value is null || value.Length <= maxLength ? value : value[..maxLength];
}

/// <param name="Rejected">
/// One sentence per sample that was not stored. Ingestion logs these and carries on — a poller that
/// sends one malformed metric must not lose the two hundred good ones in the same batch, which is
/// the same rule as "one dead device never blocks a cycle" applied to the receiving end.
/// </param>
public sealed record TelemetryIngestionPlan(
    IReadOnlyList<DeviceMetric> Metrics,
    IReadOnlyList<DeviceInventoryFact> InventoryFacts,
    IReadOnlyList<string> Rejected);
