using Contracts.Events;

using Modules.Monitoring.Features.Metrics;

namespace Infrastructure.Tests;

/// <summary>
/// What one telemetry batch becomes, with no database in the way: which samples are stored as
/// numbers, which as text, which are derived from the check result itself, and which are refused.
/// </summary>
public sealed class TelemetryIngestionPlannerTests
{
    private static readonly DateTimeOffset Observed = new(2026, 8, 10, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Plan_ForASuccessfulCheck_DerivesSuccessAndLatencyBesideItsSamples()
    {
        var plan = TelemetryIngestionPlanner.Plan(Batch(Result(
            latencyMs: 4.2,
            metrics: [new MetricSample("cpu.utilisation", 37, null, "%")])));

        Assert.Equal(
            ["check.latency_ms", "check.success", "cpu.utilisation"],
            plan.Metrics.Select(metric => metric.MetricName).Order());
        Assert.Equal(1d, plan.Metrics.Single(metric => metric.MetricName == "check.success").Value);
        Assert.Equal(4.2, plan.Metrics.Single(metric => metric.MetricName == "check.latency_ms").Value);
        Assert.Equal("ms", plan.Metrics.Single(metric => metric.MetricName == "check.latency_ms").Unit);
        Assert.Equal("%", plan.Metrics.Single(metric => metric.MetricName == "cpu.utilisation").Unit);
        Assert.Empty(plan.Rejected);
    }

    /// <summary>
    /// A failed check carries no samples, so the derived row is the only record it ran at all —
    /// without it an unreachable device is indistinguishable from one nobody polls.
    /// </summary>
    [Fact]
    public void Plan_ForAFailedCheck_StillRecordsThatItRan()
    {
        var plan = TelemetryIngestionPlanner.Plan(Batch(Result(
            succeeded: false,
            latencyMs: null,
            error: "Timed out after 5s",
            metrics: [])));

        var metric = Assert.Single(plan.Metrics);
        Assert.Equal("check.success", metric.MetricName);
        Assert.Equal(0d, metric.Value);
        Assert.Empty(plan.InventoryFacts);
    }

    [Fact]
    public void Plan_ForATextSample_ProducesAnInventoryFactRatherThanAMetric()
    {
        var plan = TelemetryIngestionPlanner.Plan(Batch(Result(
            metrics: [new MetricSample("sysName", null, "core-sw-01", null)])));

        Assert.DoesNotContain(plan.Metrics, metric => metric.MetricName == "sysName");
        var fact = Assert.Single(plan.InventoryFacts);
        Assert.Equal("sysName", fact.Name);
        Assert.Equal("core-sw-01", fact.Value);
        Assert.Equal(Observed, fact.ObservedAt);
    }

    /// <summary>
    /// Two checks on one device can both report sysName. The later reading wins, and — the point of
    /// the comparison — an earlier one arriving second does not overwrite it.
    /// </summary>
    [Fact]
    public void Plan_ForTheSameFactTwice_KeepsTheLaterReading()
    {
        var plan = TelemetryIngestionPlanner.Plan(Batch(
            Result(observedAt: Observed.AddSeconds(30),
                metrics: [new MetricSample("sysName", null, "renamed", null)]),
            Result(observedAt: Observed,
                metrics: [new MetricSample("sysName", null, "original", null)])));

        var fact = Assert.Single(plan.InventoryFacts);
        Assert.Equal("renamed", fact.Value);
    }

    // ---- failure paths ----

    /// <summary>
    /// The derived names are reserved, or "did this check succeed" would mean whatever the last
    /// poller to publish decided it meant.
    /// </summary>
    [Fact]
    public void Plan_ForASampleUsingTheReservedPrefix_RejectsItAndKeepsTheDerivedOne()
    {
        var plan = TelemetryIngestionPlanner.Plan(Batch(Result(
            metrics: [new MetricSample("check.success", 0, null, null)])));

        // One check.success row, and it is the derived one — the poller's 0 did not overwrite it.
        var success = Assert.Single(plan.Metrics, metric => metric.MetricName == "check.success");
        Assert.Equal(1d, success.Value);
        Assert.Contains(plan.Rejected, message => message.Contains("reserved", StringComparison.Ordinal));
    }

    [Fact]
    public void Plan_ForASampleCarryingBothAValueAndText_RejectsIt()
    {
        var plan = TelemetryIngestionPlanner.Plan(Batch(Result(
            metrics: [new MetricSample("sysDescr", 1, "both", null)])));

        Assert.Empty(plan.InventoryFacts);
        Assert.DoesNotContain(plan.Metrics, metric => metric.MetricName == "sysDescr");
        Assert.Contains(plan.Rejected, message => message.Contains("either a value or text", StringComparison.Ordinal));
    }

    [Fact]
    public void Plan_ForANonFiniteValue_RejectsIt()
    {
        var plan = TelemetryIngestionPlanner.Plan(Batch(Result(
            metrics: [new MetricSample("memory.used", double.NaN, null, "bytes")])));

        Assert.DoesNotContain(plan.Metrics, metric => metric.MetricName == "memory.used");
        Assert.Contains(plan.Rejected, message => message.Contains("either a value or text", StringComparison.Ordinal));
    }

    /// <summary>One bad sample must not lose the good ones beside it.</summary>
    [Fact]
    public void Plan_WithOneBadSample_StillStoresTheRest()
    {
        var plan = TelemetryIngestionPlanner.Plan(Batch(Result(
            metrics:
            [
                new MetricSample("cpu.utilisation", 37, null, "%"),
                new MetricSample("  ", 1, null, null),
                new MetricSample("memory.utilisation", 61, null, "%"),
            ])));

        Assert.Contains(plan.Metrics, metric => metric.MetricName == "cpu.utilisation");
        Assert.Contains(plan.Metrics, metric => metric.MetricName == "memory.utilisation");
        Assert.Single(plan.Rejected);
    }

    private static DeviceTelemetryReported Batch(params DeviceCheckResult[] results) => new(
        Guid.CreateVersion7(),
        Observed,
        "poller-1",
        "default",
        CycleNumber: 7,
        results);

    private static DeviceCheckResult Result(
        bool succeeded = true,
        double? latencyMs = 1.5,
        string? error = null,
        DateTimeOffset? observedAt = null,
        IReadOnlyList<MetricSample>? metrics = null) => new(
        DeviceId: DeviceId,
        CiId: CiId,
        CheckId: CheckId,
        CheckType: "Snmp",
        CheckName: "CPU",
        Address: "10.20.0.1",
        ObservedAt: observedAt ?? Observed,
        Succeeded: succeeded,
        LatencyMs: latencyMs,
        Error: error,
        Metrics: metrics ?? []);

    private static readonly Guid DeviceId = Guid.CreateVersion7();
    private static readonly Guid CiId = Guid.CreateVersion7();
    private static readonly Guid CheckId = Guid.CreateVersion7();
}
