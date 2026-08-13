using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Modules.Monitoring.Features.Discovery;

/// <summary>
/// The forms a scan range may take, and how many addresses each one probes.
/// <para>
/// This validates and counts; it never enumerates. Turning <c>10.0.0.0/24</c> into 254 addresses is
/// the scanner's job and happens in the scanner's process — an API that materialised a range would be
/// one <c>10.0.0.0/8</c> away from a 16-million-element list in a web request.
/// </para>
/// <para>
/// The accepted forms are mirrored by hand in <c>services/discovery/src/discovery/ranges.py</c>, and
/// nothing cross-checks the two automatically — the same standing hazard WP-3.8 recorded for check
/// parameters. Both sides are deliberately small and both are unit-tested against the same counts, and
/// a form added to one without the other is refused at this edge rather than silently unscanned.
/// </para>
/// </summary>
public static class ScanRange
{
    /// <summary>
    /// The subnet the scanner itself is attached to, resolved where it runs.
    /// <para>
    /// It exists because the useful range is genuinely not knowable here: under <c>aspire run</c> the
    /// scanner sits on a container network whose subnet Docker allocates at session start, so a seeded
    /// profile with a literal CIDR would scan an address space nothing is on. In a real deployment it
    /// is also the range most operators mean by "scan the office".
    /// </para>
    /// </summary>
    public const string LocalKeyword = "local";

    /// <summary>A /16 of probes. Above this a scan is a sweep somebody should have to say out loud.</summary>
    public const long MaximumAddressesPerRange = 65_536;

    /// <summary>The same ceiling across every range on one profile.</summary>
    public const long MaximumAddressesPerProfile = 65_536;

    public const int MaximumRanges = 50;

    /// <summary>
    /// What a range string turned out to be. <paramref name="AddressCount"/> is null for
    /// <see cref="LocalKeyword"/>, whose size depends on the interface the scanner finds.
    /// </summary>
    public sealed record ParsedRange(string Text, long? AddressCount);

    /// <summary>
    /// Parses one range, answering the sentence that is wrong with it rather than a boolean.
    /// <para>
    /// The message is the whole value of this method: "10.0.0.0/8 is 16,777,214 addresses, which is
    /// above the limit of 65,536" tells an operator what to type next, while "invalid range" starts a
    /// conversation.
    /// </para>
    /// </summary>
    public static ParsedRange? TryParse(string? raw, out string? error)
    {
        error = null;
        var text = (raw ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            error = "A range cannot be empty.";
            return null;
        }

        if (string.Equals(text, LocalKeyword, StringComparison.OrdinalIgnoreCase))
        {
            return new ParsedRange(LocalKeyword, null);
        }

        var parsed = text.Contains('/', StringComparison.Ordinal)
            ? ParseCidr(text, out error)
            : text.Contains('-', StringComparison.Ordinal)
                ? ParseSpan(text, out error)
                : ParseSingle(text, out error);

        if (parsed is null)
        {
            return null;
        }

        if (parsed.AddressCount > MaximumAddressesPerRange)
        {
            error = $"'{text}' is {parsed.AddressCount.Value.ToString("N0", CultureInfo.InvariantCulture)} "
                + $"addresses, which is above the limit of "
                + $"{MaximumAddressesPerRange.ToString("N0", CultureInfo.InvariantCulture)}.";
            return null;
        }

        return parsed;
    }

    /// <summary>
    /// The total a profile's ranges probe, or null when any of them is <see cref="LocalKeyword"/> —
    /// a partial total presented as a total is worse than no number at all.
    /// </summary>
    public static long? TotalAddresses(IEnumerable<ParsedRange> ranges)
    {
        ArgumentNullException.ThrowIfNull(ranges);

        long total = 0;
        foreach (var range in ranges)
        {
            if (range.AddressCount is not { } count)
            {
                return null;
            }

            total += count;
        }

        return total;
    }

    private static ParsedRange? ParseCidr(string text, out string? error)
    {
        error = null;
        var parts = text.Split('/', 2);
        // The address is validated and then discarded: how many addresses a block holds depends only
        // on its prefix, and a host address inside the block (`10.0.0.5/24`) is accepted and normalised
        // by the scanner exactly as `ipaddress.IPv4Network(strict=False)` does on the other side.
        if (!TryParseIPv4(parts[0], out _))
        {
            error = $"'{parts[0]}' is not an IPv4 address.";
            return null;
        }

        if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var prefix)
            || prefix is < 0 or > 32)
        {
            error = $"'{parts[1]}' is not a prefix length between 0 and 32.";
            return null;
        }

        var size = 1L << (32 - prefix);

        // A /31 is a point-to-point link and a /32 is one host: both address every value in the block.
        // Anything wider reserves the first and last for the network and the broadcast, and probing
        // them finds nothing while making every scan's count read two too high.
        return new ParsedRange(text, prefix >= 31 ? size : size - 2);
    }

    private static ParsedRange? ParseSpan(string text, out string? error)
    {
        error = null;
        var parts = text.Split('-', 2);
        if (!TryParseIPv4(parts[0], out var first))
        {
            error = $"'{parts[0]}' is not an IPv4 address.";
            return null;
        }

        var octets = first.GetAddressBytes();
        var last = parts[1].Trim();

        // Either "10.0.0.5-40" (the last octet) or "10.0.0.5-10.0.0.40" (a whole address). Both are
        // spellings people actually use, and refusing one of them wins nothing.
        int lastOctet;
        if (last.Contains('.', StringComparison.Ordinal))
        {
            if (!TryParseIPv4(last, out var lastAddress))
            {
                error = $"'{last}' is not an IPv4 address.";
                return null;
            }

            var lastOctets = lastAddress.GetAddressBytes();
            if (!lastOctets.AsSpan(0, 3).SequenceEqual(octets.AsSpan(0, 3)))
            {
                error = $"'{text}' spans more than one /24, which a range of this form cannot express.";
                return null;
            }

            lastOctet = lastOctets[3];
        }
        else if (!int.TryParse(last, NumberStyles.None, CultureInfo.InvariantCulture, out lastOctet)
            || lastOctet > 255)
        {
            error = $"'{last}' is not a final octet between 0 and 255.";
            return null;
        }

        if (lastOctet < octets[3])
        {
            error = $"'{text}' ends before it starts.";
            return null;
        }

        return new ParsedRange(text, lastOctet - octets[3] + 1);
    }

    private static ParsedRange? ParseSingle(string text, out string? error)
    {
        error = null;
        if (TryParseIPv4(text, out _))
        {
            return new ParsedRange(text, 1);
        }

        error = $"'{text}' is not an IPv4 address, a CIDR block, a range, or '{LocalKeyword}'.";
        return null;
    }

    /// <summary>
    /// IPv4 only, and dotted-quad only.
    /// <para>
    /// <see cref="IPAddress.TryParse(string, out IPAddress)"/> alone accepts "10" as 0.0.0.10 and
    /// would turn a typo into a range nobody meant — the same reason WP-2.1 canonicalises a CI's
    /// management IP rather than trusting a regex. IPv6 is refused because an ICMP sweep of an IPv6
    /// subnet is not a thing that terminates.
    /// </para>
    /// </summary>
    private static bool TryParseIPv4(string text, out IPAddress address)
    {
        address = IPAddress.None;
        var trimmed = text.Trim();
        if (trimmed.Split('.').Length != 4)
        {
            return false;
        }

        if (!IPAddress.TryParse(trimmed, out var parsed)
            || parsed.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        address = parsed;
        return true;
    }
}
