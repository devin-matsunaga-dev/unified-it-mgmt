using System.Globalization;

namespace Modules.Monitoring.Features.Discovery;

/// <summary>
/// What makes a scan profile coherent. Cross-field rules — a timeout that outlives the interval, a
/// range list that adds up to more probes than the ceiling allows — so they live here rather than in a
/// FluentValidation rule set that sees one property at a time, exactly as <c>CheckRules</c> does for a
/// check. Pure and infrastructure-free, so the whole matrix is unit-testable.
/// </summary>
public static class ScanProfileRules
{
    public const int MinimumIntervalMinutes = 1;
    public const int MaximumIntervalMinutes = 10_080;
    public const int MinimumTimeoutSeconds = 1;
    public const int MaximumTimeoutSeconds = 60;
    public const int MaximumPorts = 20;

    public const int MinimumPort = 1;
    public const int MaximumPort = 65_535;

    public static IReadOnlyDictionary<string, string[]> Validate(
        IReadOnlyList<string>? ranges,
        IReadOnlyList<int>? ports,
        int intervalMinutes,
        int timeoutSeconds)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        ValidateRanges(ranges, errors);
        ValidatePorts(ports, errors);

        if (intervalMinutes is < MinimumIntervalMinutes or > MaximumIntervalMinutes)
        {
            errors["IntervalMinutes"] =
                [$"Interval must be between {MinimumIntervalMinutes} and {MaximumIntervalMinutes} minutes."];
        }

        if (timeoutSeconds is < MinimumTimeoutSeconds or > MaximumTimeoutSeconds)
        {
            errors["TimeoutSeconds"] =
                [$"Timeout must be between {MinimumTimeoutSeconds} and {MaximumTimeoutSeconds} seconds."];
        }

        return errors;
    }

    /// <summary>
    /// Parses every range, keeping the order they were written in. Returns null when any of them is
    /// bad — the caller has already had the reasons from <see cref="Validate"/>.
    /// </summary>
    public static IReadOnlyList<ScanRange.ParsedRange>? Parse(IReadOnlyList<string>? ranges)
    {
        if (ranges is null)
        {
            return null;
        }

        var parsed = new List<ScanRange.ParsedRange>(ranges.Count);
        foreach (var range in ranges)
        {
            if (ScanRange.TryParse(range, out _) is not { } item)
            {
                return null;
            }

            parsed.Add(item);
        }

        return parsed;
    }

    private static void ValidateRanges(
        IReadOnlyList<string>? ranges,
        Dictionary<string, string[]> errors)
    {
        if (ranges is null || ranges.Count == 0)
        {
            // A profile with no range is a scan of nothing that still runs on a schedule and reports
            // success, which is the most expensive kind of broken.
            errors["Ranges"] = ["A scan profile needs at least one range to scan."];
            return;
        }

        if (ranges.Count > ScanRange.MaximumRanges)
        {
            errors["Ranges"] = [$"A scan profile may name at most {ScanRange.MaximumRanges} ranges."];
            return;
        }

        var messages = new List<string>();
        var parsed = new List<ScanRange.ParsedRange>(ranges.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var range in ranges)
        {
            if (ScanRange.TryParse(range, out var error) is not { } item)
            {
                messages.Add(error!);
                continue;
            }

            // Scanning the same block twice does not find more devices; it doubles the probes and
            // publishes each address twice, which reads downstream as two discoveries.
            if (!seen.Add(item.Text))
            {
                messages.Add($"'{item.Text}' is named more than once.");
                continue;
            }

            parsed.Add(item);
        }

        if (messages.Count == 0
            && ScanRange.TotalAddresses(parsed) is { } total
            && total > ScanRange.MaximumAddressesPerProfile)
        {
            messages.Add(
                $"These ranges add up to {total.ToString("N0", CultureInfo.InvariantCulture)} addresses, "
                + $"which is above the limit of "
                + $"{ScanRange.MaximumAddressesPerProfile.ToString("N0", CultureInfo.InvariantCulture)}.");
        }

        if (messages.Count > 0)
        {
            errors["Ranges"] = [.. messages];
        }
    }

    private static void ValidatePorts(IReadOnlyList<int>? ports, Dictionary<string, string[]> errors)
    {
        // An empty list is legal and means an ICMP-only sweep, which is the cheapest useful scan.
        if (ports is null || ports.Count == 0)
        {
            return;
        }

        if (ports.Count > MaximumPorts)
        {
            errors["Ports"] = [$"A scan profile may fingerprint at most {MaximumPorts} ports."];
            return;
        }

        var messages = new List<string>();
        foreach (var port in ports.Where(port => port is < MinimumPort or > MaximumPort))
        {
            messages.Add($"Port {port.ToString(CultureInfo.InvariantCulture)} is outside "
                + $"{MinimumPort}–{MaximumPort}.");
        }

        if (ports.Distinct().Count() != ports.Count)
        {
            messages.Add("The same port is named more than once.");
        }

        if (messages.Count > 0)
        {
            errors["Ports"] = [.. messages];
        }
    }
}
