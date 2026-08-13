using Contracts.Events;

using Modules.Monitoring.Data;
using Modules.Monitoring.Features.Interfaces;

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
    private const int MaxInterfaceNameLength = 100;
    private const int MaxInterfaceAliasLength = 200;

    public static TelemetryIngestionPlan Plan(DeviceTelemetryReported telemetry)
    {
        ArgumentNullException.ThrowIfNull(telemetry);

        var metrics = new List<DeviceMetric>();
        var facts = new Dictionary<(Guid DeviceId, string Name), DeviceInventoryFact>();
        var interfaces = new Dictionary<(Guid DeviceId, int IfIndex), DeviceInterface>();
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

                // An interface sample is filed twice over: the number goes to the hypertable like any
                // other, because a per-interface chart is an ordinary series query, and the fields
                // that describe the port fold into its row so the interface table renders in one
                // read. The text ones — a name, an alias — go only to the row: forty-eight ports
                // would otherwise put a hundred and fifty entries in an inventory card built to show
                // a device's sysDescr.
                var interfaceField = false;
                if (InterfaceMetricNames.TryParse(name, out var ifIndex, out var field))
                {
                    interfaceField = true;
                    Fold(interfaces, result, ifIndex, field, sample);
                }

                if (hasValue)
                {
                    metrics.Add(NewMetric(result, name, sample.Value!.Value, Truncate(sample.Unit, MaxUnitLength), pollerName));
                    continue;
                }

                if (interfaceField)
                {
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

        return new TelemetryIngestionPlan(metrics, [.. facts.Values], [.. interfaces.Values], rejected);
    }

    /// <summary>
    /// Folds one sample into the interface row it describes, creating the row on first sight.
    /// <para>
    /// An interface arrives as a dozen separate samples in one batch — its name, its statuses, its
    /// rates — and this is what makes them one record again. A field the poller did not send this
    /// cycle is left null rather than carried over from the previous poll: the row says what the
    /// last poll found, so a rate that stopped being measurable (a restarted poller has no baseline)
    /// must read as absent rather than as the number it was ten minutes ago.
    /// </para>
    /// </summary>
    private static void Fold(
        Dictionary<(Guid DeviceId, int IfIndex), DeviceInterface> interfaces,
        DeviceCheckResult result,
        int ifIndex,
        string field,
        MetricSample sample)
    {
        var key = (result.DeviceId, ifIndex);
        if (!interfaces.TryGetValue(key, out var link))
        {
            link = new DeviceInterface
            {
                DeviceId = result.DeviceId,
                IfIndex = ifIndex,
                CheckId = result.CheckId,
                ObservedAt = result.ObservedAt.ToUniversalTime(),
            };
            interfaces[key] = link;
        }
        else if (result.ObservedAt.ToUniversalTime() < link.ObservedAt)
        {
            // Two checks, or two cycles of one, inside a single batch. The later reading owns the
            // row for the same reason an inventory fact only moves forwards.
            return;
        }
        else
        {
            link.ObservedAt = result.ObservedAt.ToUniversalTime();
            link.CheckId = result.CheckId;
        }

        var text = sample.Text?.Trim();
        var value = sample.Value;
        switch (field)
        {
            case InterfaceMetricNames.Name: link.Name = Truncate(text, MaxInterfaceNameLength); break;
            case InterfaceMetricNames.Alias: link.Alias = Truncate(text, MaxInterfaceAliasLength); break;
            case InterfaceMetricNames.MacAddress: link.MacAddress = Truncate(text, MaxInterfaceNameLength); break;
            case InterfaceMetricNames.InterfaceType: link.InterfaceType = AsInt(value); break;
            case InterfaceMetricNames.AdminStatus: link.AdminStatus = AsStatus(value); break;
            case InterfaceMetricNames.OperStatus: link.OperStatus = AsStatus(value); break;
            case InterfaceMetricNames.Speed: link.SpeedBitsPerSecond = AsLong(value); break;
            case InterfaceMetricNames.BitsIn: link.BitsInPerSecond = value; break;
            case InterfaceMetricNames.BitsOut: link.BitsOutPerSecond = value; break;
            case InterfaceMetricNames.Utilisation: link.UtilisationPercent = value; break;
            case InterfaceMetricNames.ErrorsIn: link.ErrorsInPerSecond = value; break;
            case InterfaceMetricNames.ErrorsOut: link.ErrorsOutPerSecond = value; break;
            case InterfaceMetricNames.DiscardsIn: link.DiscardsInPerSecond = value; break;
            case InterfaceMetricNames.DiscardsOut: link.DiscardsOutPerSecond = value; break;
            // A field this version does not know. Its number still reaches the hypertable, so a
            // poller ahead of the platform loses a column of the table and none of its history.
            default: break;
        }
    }

    /// <summary>
    /// An IF-MIB status number as the enum, with anything outside it reading as unknown. A vendor's
    /// private value must not fail the batch that the other forty-seven ports arrived in.
    /// </summary>
    private static InterfaceStatus AsStatus(double? value) =>
        AsInt(value) is { } number && Enum.IsDefined(typeof(InterfaceStatus), number)
            ? (InterfaceStatus)number
            : InterfaceStatus.Unknown;

    private static int? AsInt(double? value) =>
        value is { } number && double.IsFinite(number) && number is > int.MinValue and < int.MaxValue
            ? (int)number
            : null;

    private static long? AsLong(double? value) =>
        value is { } number && double.IsFinite(number) && number is > long.MinValue and < long.MaxValue
            ? (long)number
            : null;

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
    IReadOnlyList<DeviceInterface> Interfaces,
    IReadOnlyList<string> Rejected);
