using Modules.Monitoring.Data;
using Modules.Monitoring.Features.Devices;

namespace Infrastructure.Tests;

/// <summary>
/// The cross-field rules a check definition has to satisfy. No infrastructure: these are statements
/// about the relationship between interval, timeout, thresholds and per-type parameters.
/// </summary>
public sealed class CheckRulesTests
{
    [Fact]
    public void Validate_AWellFormedIcmpCheck_ReportsNothing()
    {
        var errors = Validate(CheckType.Icmp, intervalSeconds: 60, timeoutSeconds: 5);

        Assert.Empty(errors);
    }

    [Theory]
    [InlineData(CheckRules.MinimumIntervalSeconds - 1)]
    [InlineData(CheckRules.MaximumIntervalSeconds + 1)]
    public void Validate_IntervalOutsideTheAllowedRange_IsRejected(int intervalSeconds)
    {
        var errors = Validate(CheckType.Icmp, intervalSeconds, timeoutSeconds: 5);

        Assert.Contains("IntervalSeconds", errors.Keys);
    }

    /// <summary>
    /// A check still running when its next run falls due either overlaps itself or skips a cycle, so
    /// the interval it actually reports at is not the one it was configured with.
    /// </summary>
    [Fact]
    public void Validate_TimeoutNotShorterThanTheInterval_IsRejected()
    {
        var errors = Validate(CheckType.Icmp, intervalSeconds: 30, timeoutSeconds: 30);

        Assert.Contains("Timeout must be shorter than the interval.", errors["TimeoutSeconds"]);
    }

    [Fact]
    public void Validate_RisingCheckWithCriticalBelowWarning_IsRejected()
    {
        var errors = Validate(
            CheckType.Icmp, 60, 5,
            warningThreshold: 200, criticalThreshold: 100, comparison: ThresholdComparison.GreaterThan);

        Assert.Contains("CriticalThreshold", errors.Keys);
    }

    /// <summary>Free disk falls, so its critical threshold sits below the warning one.</summary>
    [Fact]
    public void Validate_FallingCheckWithCriticalBelowWarning_IsAccepted()
    {
        var errors = Validate(
            CheckType.Snmp, 300, 10,
            warningThreshold: 20, criticalThreshold: 10, comparison: ThresholdComparison.LessThan,
            parameters: new Dictionary<string, string> { ["oid"] = "1.3.6.1.4.1.2021.9.1.7.1" });

        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_FallingCheckWithCriticalAboveWarning_IsRejected()
    {
        var errors = Validate(
            CheckType.Snmp, 300, 10,
            warningThreshold: 10, criticalThreshold: 20, comparison: ThresholdComparison.LessThan,
            parameters: new Dictionary<string, string> { ["oid"] = "1.3.6.1.4.1.2021.9.1.7.1" });

        Assert.Contains("CriticalThreshold", errors.Keys);
    }

    /// <summary>One threshold on its own is a complete statement; only a pair can be misordered.</summary>
    [Fact]
    public void Validate_OnlyOneThresholdSet_IsAccepted()
    {
        var errors = Validate(CheckType.Icmp, 60, 5, criticalThreshold: 500);

        Assert.Empty(errors);
    }

    [Theory]
    [InlineData(CheckType.Snmp, "oid")]
    [InlineData(CheckType.Tcp, "port")]
    [InlineData(CheckType.Http, "url")]
    public void Validate_TypeMissingItsRequiredParameter_IsRejected(CheckType type, string required)
    {
        var errors = Validate(type, 60, 5);

        Assert.Contains($"A {type} check requires a '{required}' parameter.", errors["Parameters"]);
    }

    [Fact]
    public void Validate_RequiredParameterPresentButBlank_IsRejected()
    {
        var errors = Validate(
            CheckType.Snmp, 60, 5,
            parameters: new Dictionary<string, string> { ["oid"] = "   " });

        Assert.Contains("Parameters", errors.Keys);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("https")]
    public void Validate_TcpCheckWithAPortThatIsNotAPort_IsRejected(string port)
    {
        var errors = Validate(
            CheckType.Tcp, 60, 5,
            parameters: new Dictionary<string, string> { ["port"] = port });

        Assert.Contains("'port' must be a TCP port between 1 and 65535.", errors["Parameters"]);
    }

    [Theory]
    [InlineData("mailhog:8025")]
    [InlineData("ftp://files.example.test")]
    public void Validate_HttpCheckWithoutAnAbsoluteHttpUrl_IsRejected(string url)
    {
        var errors = Validate(
            CheckType.Http, 60, 5,
            parameters: new Dictionary<string, string> { ["url"] = url });

        Assert.Contains("'url' must be an absolute http or https URL.", errors["Parameters"]);
    }

    [Fact]
    public void Validate_HttpCheckWithAnAbsoluteUrl_IsAccepted()
    {
        var errors = Validate(
            CheckType.Http, 60, 5,
            parameters: new Dictionary<string, string> { ["url"] = "http://mailhog:8025/" });

        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_MoreParametersThanAllowed_IsRejected()
    {
        var parameters = Enumerable.Range(0, CheckRules.MaximumParameters + 1)
            .ToDictionary(index => $"key{index}", _ => "value");

        var errors = Validate(CheckType.Icmp, 60, 5, parameters: parameters);

        Assert.Contains("Parameters", errors.Keys);
    }

    private static IReadOnlyDictionary<string, string[]> Validate(
        CheckType type,
        int intervalSeconds,
        int timeoutSeconds,
        double? warningThreshold = null,
        double? criticalThreshold = null,
        ThresholdComparison comparison = ThresholdComparison.GreaterThan,
        IReadOnlyDictionary<string, string>? parameters = null) =>
        CheckRules.Validate(
            type, intervalSeconds, timeoutSeconds, warningThreshold, criticalThreshold, comparison,
            parameters ?? new Dictionary<string, string>(StringComparer.Ordinal));
}
