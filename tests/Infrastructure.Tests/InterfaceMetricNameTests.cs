using Modules.Monitoring.Features.Interfaces;

namespace Infrastructure.Tests;

/// <summary>
/// The one place a metric name is taken apart into an interface and a field.
/// <para>
/// A metric row is <c>(time, device, check, name)</c> and carries no labels, so the interface a
/// sample is about lives in its name. That keeps the hypertable, the series API and WP-3.4's picker
/// working unchanged — and puts the whole weight of the arrangement on this parser, which is why
/// the exact strings are pinned here and again in the poller's <c>test_checks_interfaces.py</c>.
/// </para>
/// </summary>
public sealed class InterfaceMetricNameTests
{
    [Fact]
    public void For_BuildsTheNameThePollerPublishes() =>
        Assert.Equal("interface.3.bits_in_per_second", InterfaceMetricNames.For(3, InterfaceMetricNames.BitsIn));

    /// <summary>
    /// The prefix the response hands the browser, so the shape of a metric name is knowledge one
    /// module holds and not three.
    /// </summary>
    [Fact]
    public void For_WithNoField_IsTheInterfacesOwnPrefix() =>
        Assert.Equal("interface.3.", InterfaceMetricNames.For(3, string.Empty));

    [Fact]
    public void TryParse_ForAnInterfaceMetric_SplitsTheIndexFromTheField()
    {
        Assert.True(InterfaceMetricNames.TryParse("interface.12.oper_status", out var ifIndex, out var field));

        Assert.Equal(12, ifIndex);
        Assert.Equal(InterfaceMetricNames.OperStatus, field);
    }

    [Theory]
    // Not an interface metric at all.
    [InlineData("cpu.utilisation_percent")]
    [InlineData("interfaces.1.name")]
    [InlineData("interface.1")]
    [InlineData("interface..name")]
    [InlineData("interface.1.")]
    // An index no device has. A stray sample must not create an interface row for port zero.
    [InlineData("interface.0.name")]
    [InlineData("interface.-3.name")]
    [InlineData("interface.1e3.name")]
    // A field with a dot in it: a name this platform did not publish, whatever it looks like.
    [InlineData("interface.1.errors.in")]
    public void TryParse_ForAnythingElse_Refuses(string metricName)
    {
        Assert.False(InterfaceMetricNames.TryParse(metricName, out _, out _));
        Assert.False(InterfaceMetricNames.IsInterfaceMetric(metricName));
    }

    /// <summary>
    /// Every field the poller sends round-trips. This is the assertion that fails if somebody renames
    /// one on this side only — the other side is Python and will not stop compiling.
    /// </summary>
    [Theory]
    [InlineData("name")]
    [InlineData("alias")]
    [InlineData("mac_address")]
    [InlineData("type")]
    [InlineData("admin_status")]
    [InlineData("oper_status")]
    [InlineData("speed_bits_per_second")]
    [InlineData("bits_in_per_second")]
    [InlineData("bits_out_per_second")]
    [InlineData("utilisation_percent")]
    [InlineData("errors_in_per_second")]
    [InlineData("errors_out_per_second")]
    [InlineData("discards_in_per_second")]
    [InlineData("discards_out_per_second")]
    public void TryParse_ForEveryFieldThePollerPublishes_RoundTrips(string field)
    {
        Assert.True(InterfaceMetricNames.TryParse(InterfaceMetricNames.For(7, field), out var ifIndex, out var parsed));

        Assert.Equal(7, ifIndex);
        Assert.Equal(field, parsed);
    }
}
