using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Modules.Monitoring.Features.Interfaces;

/// <summary>
/// The names an interface poll publishes its samples under, and the one place they are taken apart
/// again.
/// <para>
/// A metric is identified by <c>(time, device, check, name)</c> and carries no labels, so the
/// interface an interface metric is about lives in its name: <c>interface.3.bits_in_per_second</c>
/// is port 3's inbound traffic. That keeps the hypertable, the series API and WP-3.4's picker
/// working unchanged — a per-interface chart is an ordinary series query — at the cost of this
/// parser, which is the only code allowed to know the shape.
/// </para>
/// <para>
/// The names mirror <c>services/poller/src/poller/checks/interfaces.py</c> by hand, which is the
/// standing hazard <see cref="Alerting.AlertRules.PrimaryMetric"/> already carries: if the poller
/// renames a field, nothing fails to compile and interfaces quietly stop being recorded.
/// <c>InterfaceMetricNameTests</c> pins the exact strings on this side, and the poller's
/// <c>test_checks_interfaces.py</c> pins them on the other.
/// </para>
/// </summary>
public static class InterfaceMetricNames
{
    /// <summary>What every interface metric name starts with. Also what keeps them out of the inventory facts.</summary>
    public const string Prefix = "interface.";

    public const string Name = "name";
    public const string Alias = "alias";
    public const string MacAddress = "mac_address";
    public const string InterfaceType = "type";
    public const string AdminStatus = "admin_status";
    public const string OperStatus = "oper_status";
    public const string Speed = "speed_bits_per_second";
    public const string BitsIn = "bits_in_per_second";
    public const string BitsOut = "bits_out_per_second";
    public const string Utilisation = "utilisation_percent";
    public const string ErrorsIn = "errors_in_per_second";
    public const string ErrorsOut = "errors_out_per_second";
    public const string DiscardsIn = "discards_in_per_second";
    public const string DiscardsOut = "discards_out_per_second";

    /// <summary>The metric name a field of one interface is published under.</summary>
    public static string For(int ifIndex, string field) =>
        string.Create(CultureInfo.InvariantCulture, $"{Prefix}{ifIndex}.{field}");

    /// <summary>Whether a metric name is one of an interface's, without caring which field.</summary>
    public static bool IsInterfaceMetric(string metricName) =>
        TryParse(metricName, out _, out _);

    /// <summary>
    /// Splits <c>interface.&lt;ifIndex&gt;.&lt;field&gt;</c>, refusing anything else.
    /// <para>
    /// Strict about both halves: an index that is not a positive number, or a field carrying a
    /// further dot, is a name this platform did not publish. Treating it as an interface anyway
    /// would let a poller with a stray metric create an interface row for port -1.
    /// </para>
    /// </summary>
    public static bool TryParse(
        string? metricName,
        out int ifIndex,
        [NotNullWhen(true)] out string? field)
    {
        ifIndex = 0;
        field = null;
        if (metricName is null || !metricName.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var rest = metricName.AsSpan(Prefix.Length);
        var separator = rest.IndexOf('.');
        if (separator <= 0 || separator == rest.Length - 1)
        {
            return false;
        }

        if (!int.TryParse(rest[..separator], NumberStyles.None, CultureInfo.InvariantCulture, out var index)
            || index <= 0)
        {
            return false;
        }

        var name = rest[(separator + 1)..];
        if (name.Contains('.'))
        {
            return false;
        }

        ifIndex = index;
        field = name.ToString();
        return true;
    }
}
