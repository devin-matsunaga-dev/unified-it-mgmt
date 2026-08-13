using Modules.Monitoring.Features.Discovery;

namespace Infrastructure.Tests;

/// <summary>
/// The scan-profile validation matrix, infrastructure-free.
/// <para>
/// The address counts here are also the cross-language contract: <c>ScanRange</c> and
/// <c>services/discovery/src/discovery/ranges.py</c> mirror each other by hand, and nothing checks
/// them automatically — the same standing hazard WP-3.8 recorded for check parameters. The Python
/// suite's <c>test_ranges.py</c> asserts the same numbers for the same strings, so a drift in either
/// direction shows up as one of the two suites failing rather than as a range nobody scans.
/// </para>
/// </summary>
public sealed class ScanProfileRulesTests
{
    [Theory]
    // Anything wider than a /31 loses its network and broadcast addresses.
    [InlineData("10.0.0.0/24", 254)]
    [InlineData("10.0.0.0/29", 6)]
    // A /31 is a point-to-point link and a /32 is one host: every value in the block is a host.
    [InlineData("10.0.0.4/31", 2)]
    [InlineData("10.0.0.7/32", 1)]
    [InlineData("10.0.0.5-8", 4)]
    [InlineData("10.0.0.5-10.0.0.7", 3)]
    [InlineData("192.0.2.1", 1)]
    public void Range_EveryAcceptedForm_CountsTheAddressesItProbes(string text, long expected)
    {
        var parsed = ScanRange.TryParse(text, out var error);

        Assert.Null(error);
        Assert.NotNull(parsed);
        Assert.Equal(expected, parsed.AddressCount);
    }

    [Fact]
    public void Range_TheLocalKeyword_HasNoCountUntilItIsResolvedWhereTheScannerRuns()
    {
        var parsed = ScanRange.TryParse("LOCAL", out var error);

        Assert.Null(error);
        Assert.NotNull(parsed);
        // Case-insensitive on the way in, canonical on the way out, and sizeless: the subnet depends
        // on the interface the scanner finds, so a number here would be a guess presented as a fact.
        Assert.Equal(ScanRange.LocalKeyword, parsed.Text);
        Assert.Null(parsed.AddressCount);
    }

    [Theory]
    [InlineData("", "cannot be empty")]
    [InlineData("   ", "cannot be empty")]
    // Not "0.0.0.10", which is what a bare IPAddress.TryParse would make of it — a typo must not
    // become a range nobody meant.
    [InlineData("10", "not an IPv4 address")]
    [InlineData("10.0.0.999", "not an IPv4 address")]
    [InlineData("10.0.0.0/33", "prefix length between 0 and 32")]
    [InlineData("10.0.0.9-4", "ends before it starts")]
    [InlineData("10.0.0.1-10.0.1.5", "more than one /24")]
    [InlineData("10.0.0.1-300", "final octet between 0 and 255")]
    [InlineData("::1", "not an IPv4 address")]
    public void Range_WhatItCannotScan_IsRefusedWithASentenceNamingTheProblem(
        string text,
        string expected)
    {
        var parsed = ScanRange.TryParse(text, out var error);

        Assert.Null(parsed);
        Assert.NotNull(error);
        Assert.Contains(expected, error, StringComparison.Ordinal);
    }

    [Fact]
    public void Range_ABlockAboveTheCeiling_IsRefusedWithBothNumbersInTheMessage()
    {
        var parsed = ScanRange.TryParse("10.0.0.0/8", out var error);

        Assert.Null(parsed);
        Assert.NotNull(error);
        // The message is the whole value: "16,777,214 addresses, above the limit of 65,536" tells an
        // operator what to type next.
        Assert.Contains("16,777,214", error, StringComparison.Ordinal);
        Assert.Contains("65,536", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_AProfileWithNoRanges_IsRefused()
    {
        var errors = ScanProfileRules.Validate([], [], intervalMinutes: 60, timeoutSeconds: 2);

        // A profile with no range runs on a schedule and reports success forever, which is the most
        // expensive kind of broken.
        Assert.Contains("Ranges", errors.Keys);
    }

    [Fact]
    public void Validate_RangesThatIndividuallyFitButTogetherDoNot_AreRefused()
    {
        // 65,534 and 254: each is inside the per-range ceiling, and together they are over it.
        string[] ranges = ["10.0.0.0/16", "10.9.0.0/24"];

        var errors = ScanProfileRules.Validate(ranges, [], intervalMinutes: 60, timeoutSeconds: 2);

        Assert.Contains("Ranges", errors.Keys);
        Assert.Contains("above the limit", errors["Ranges"][0], StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_TheSameRangeTwice_IsRefused()
    {
        string[] ranges = ["10.0.0.0/24", "10.0.0.0/24"];

        var errors = ScanProfileRules.Validate(ranges, [], intervalMinutes: 60, timeoutSeconds: 2);

        // Scanning a block twice does not find more devices; it doubles the probes and publishes each
        // address twice, which reads downstream as two discoveries.
        Assert.Contains("named more than once", errors["Ranges"][0], StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_TooManyRanges_IsRefusedBeforeAnyOfThemIsParsed()
    {
        var ranges = Enumerable.Range(0, ScanRange.MaximumRanges + 1)
            .Select(index => $"10.0.{index}.0/24").ToArray();

        var errors = ScanProfileRules.Validate(ranges, [], intervalMinutes: 60, timeoutSeconds: 2);

        Assert.Single(errors["Ranges"]);
        Assert.Contains("at most", errors["Ranges"][0], StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_NoPorts_IsLegalAndMeansAnIcmpOnlySweep()
    {
        var errors = ScanProfileRules.Validate(["10.0.0.0/24"], null, 60, 2);

        Assert.Empty(errors);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65_536)]
    [InlineData(-1)]
    public void Validate_APortOutsideTheLegalRange_IsRefused(int port)
    {
        var errors = ScanProfileRules.Validate(["10.0.0.0/24"], [port], 60, 2);

        Assert.Contains("Ports", errors.Keys);
    }

    [Fact]
    public void Validate_TheSamePortTwice_IsRefused()
    {
        var errors = ScanProfileRules.Validate(["10.0.0.0/24"], [443, 443], 60, 2);

        Assert.Contains("more than once", errors["Ports"][0], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10_081)]
    public void Validate_AnIntervalOutsideTheLegalRange_IsRefused(int intervalMinutes)
    {
        var errors = ScanProfileRules.Validate(["10.0.0.0/24"], [], intervalMinutes, 2);

        Assert.Contains("IntervalMinutes", errors.Keys);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(61)]
    public void Validate_ATimeoutOutsideTheLegalRange_IsRefused(int timeoutSeconds)
    {
        var errors = ScanProfileRules.Validate(["10.0.0.0/24"], [], 60, timeoutSeconds);

        Assert.Contains("TimeoutSeconds", errors.Keys);
    }

    [Fact]
    public void Validate_AProfileThatIsFineInEveryWay_ReportsNothing()
    {
        var errors = ScanProfileRules.Validate(
            ["local", "10.0.0.0/24", "192.0.2.1-8"], [22, 443], 60, 2);

        Assert.Empty(errors);
    }

    [Fact]
    public void TotalAddresses_AnyRangeWithoutACount_MakesTheWholeTotalUnknown()
    {
        var parsed = ScanProfileRules.Parse(["local", "10.0.0.0/29"]);

        Assert.NotNull(parsed);
        // A partial total presented as a total is worse than no number at all.
        Assert.Null(ScanRange.TotalAddresses(parsed));
    }

    [Fact]
    public void TotalAddresses_RangesWithCounts_AddUp()
    {
        var parsed = ScanProfileRules.Parse(["10.0.0.0/29", "192.0.2.1"]);

        Assert.NotNull(parsed);
        Assert.Equal(7, ScanRange.TotalAddresses(parsed));
    }
}
