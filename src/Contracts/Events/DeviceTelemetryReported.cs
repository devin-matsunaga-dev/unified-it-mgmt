namespace Contracts.Events;

/// <summary>
/// One poller reporting everything it measured during one cycle. Batched rather than one message per
/// check because a poller with two hundred devices and four checks each would otherwise put eight
/// hundred messages a cycle on the bus to say the same thing.
/// <para>
/// This carries measurements only. Whether a value crosses a threshold is the alert engine's
/// question (WP-3.5), and where the numbers are stored is WP-3.4's — the poller's thresholds travel
/// with its configuration so a future poller-side evaluation is possible, but nothing here judges.
/// </para>
/// </summary>
public sealed record DeviceTelemetryReported(
    Guid EventId,
    DateTimeOffset OccurredAt,
    string PollerName,
    string PollerGroup,
    long CycleNumber,
    IReadOnlyList<DeviceCheckResult> Results);

/// <summary>
/// What one check against one device produced. A check that failed still reports — a timeout is a
/// fact about the device, and dropping it would make an unreachable device look like one nobody
/// asked about.
/// </summary>
/// <param name="Succeeded">False when the check could not complete: timeout, refusal, bad credential.</param>
/// <param name="LatencyMs">Round-trip time where the check has one; null for checks that do not measure it.</param>
/// <param name="Error">One sentence naming why it failed, present only when <paramref name="Succeeded"/> is false.</param>
/// <param name="Metrics">The samples the check produced; empty for a failed check.</param>
public sealed record DeviceCheckResult(
    Guid DeviceId,
    Guid CiId,
    Guid CheckId,
    string CheckType,
    string CheckName,
    string Address,
    DateTimeOffset ObservedAt,
    bool Succeeded,
    double? LatencyMs,
    string? Error,
    IReadOnlyList<MetricSample> Metrics);

/// <summary>
/// One measurement. <see cref="Value"/> carries the number a metric is; <see cref="Text"/> carries
/// the string an inventory fact is (sysDescr, sysName). Exactly one of the two is populated, because
/// a hypertable stores numbers and a device record stores names, and WP-3.4 has to be able to tell
/// them apart without a lookup table of metric names.
/// </summary>
public sealed record MetricSample(
    string Name,
    double? Value,
    string? Text,
    string? Unit);
