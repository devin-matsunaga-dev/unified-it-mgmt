using System.Text.RegularExpressions;

namespace Modules.Assets.Features.DeviceIdentification;

/// <summary>
/// What a scanned string appears to be. Deliberately coarse: the useful distinction is between an
/// identifier that names a <em>product</em> — reusable across every device of that model — and one
/// that names a single physical <em>device</em>. Mixing those two is how a serial number ends up in a
/// product catalogue and every later scan of an unrelated machine inherits the wrong model.
/// </summary>
public enum IdentifierKind
{
    /// <summary>Nothing here says what it is. The honest answer, and the common one.</summary>
    Unknown,

    /// <summary>Identifies one physical device: a serial number or a Dell service tag.</summary>
    SerialNumber,

    /// <summary>Identifies a product: an SKU, an HP product number, a Lenovo MTM, a Cisco PID.</summary>
    ModelIdentifier,

    /// <summary>One of our own printed labels — the CI is already registered.</summary>
    AssetLabel,
}

/// <param name="RawValue">Exactly what was scanned, kept for audit. Never normalised in place.</param>
/// <param name="Value">The value to compare and store: trimmed, upper-cased, prefix removed.</param>
public sealed record ParsedIdentifier(string RawValue, string Value, IdentifierKind Kind)
{
    /// <summary>A second identifier the same barcode carried, as Lenovo's `1S` labels do.</summary>
    public ParsedIdentifier? AlsoCarried { get; init; }
}

/// <summary>
/// Classifies a scanned barcode. Pure: no database, no network, no configuration — which is what
/// makes it exhaustively testable, and why manufacturer lookup lives somewhere else entirely.
/// <para>
/// The rule throughout is that an unrecognised value is <see cref="IdentifierKind.Unknown"/> and not a
/// guess. A wrong classification is worse than none: it puts a value in the wrong field, and on a
/// product identifier it can poison the catalogue for every device that follows.
/// </para>
/// </summary>
public static class BarcodeParser
{
    /// <summary>
    /// Matches <c>SerialNumber</c>'s column. A scanner that reads a whole shipping label can emit
    /// hundreds of characters, and nothing that long is an identifier we can use.
    /// </summary>
    public const int MaxLength = 128;

    /// <summary>
    /// Labelled prefixes seen on real hardware, and what they claim the value is. Matched
    /// case-insensitively with an optional separator, because printers are inconsistent about both.
    /// </summary>
    private static readonly (string Prefix, IdentifierKind Kind)[] Prefixes =
    [
        ("SERIAL NO", IdentifierKind.SerialNumber),
        ("SERIAL", IdentifierKind.SerialNumber),
        ("SERVICE TAG", IdentifierKind.SerialNumber),
        ("SVCTAG", IdentifierKind.SerialNumber),
        ("S/N", IdentifierKind.SerialNumber),
        ("SN", IdentifierKind.SerialNumber),
        ("PRODUCT NO", IdentifierKind.ModelIdentifier),
        ("PRODUCT", IdentifierKind.ModelIdentifier),
        ("P/N", IdentifierKind.ModelIdentifier),
        ("PN", IdentifierKind.ModelIdentifier),
        ("SKU", IdentifierKind.ModelIdentifier),
        ("MTM", IdentifierKind.ModelIdentifier),
        ("PID", IdentifierKind.ModelIdentifier),
        ("VID", IdentifierKind.ModelIdentifier),
        ("MODEL", IdentifierKind.ModelIdentifier),
    ];

    /// <summary>
    /// Lenovo and IBM print one barcode holding a machine type-model run straight into the serial,
    /// behind a `1S` data identifier. The split anchors on the <em>trailing</em> 8 because a Lenovo
    /// serial is 8 characters while the type-model runs to 7 or 10 by product line — the invariant
    /// holds where a width table would not. Verified against a real scan: 1S12RQ000KUSMZ00H8S2.
    /// </summary>
    private const int LenovoSerialLength = 8;

    private static readonly char[] Separators = [':', '-', '=', ' ', '.', '#'];

    /// <summary>
    /// Cisco prints a product identifier beside a hardware version — <c>SRW2016-K9 V01</c>, or
    /// spelled out as <c>PID: SRW2016-K9 VID: V01</c>. One barcode often carries both.
    /// <para>
    /// **The PID is kept and the VID is dropped**, deliberately: a VID is a hardware revision of the
    /// same product, so V01 and V02 are the same model and keying a catalogue on the pair would split
    /// one product into a row per revision — and then the next switch off a later line would identify
    /// as nothing.
    /// </para>
    /// </summary>
    private static readonly Regex PidVid = new(
        @"^(?:PID\s*[:=]?\s*)?(?<pid>[A-Z0-9][A-Z0-9\-_.]*)\s+(?:VID\s*[:=]?\s*)?V\d{1,3}$",
        RegexOptions.Compiled);

    private static readonly Regex Allowed = new(@"^[A-Z0-9][A-Z0-9\-_.]*$", RegexOptions.Compiled);

    /// <summary>
    /// Reads one scanned value. Returns null when the input is unusable — empty, over
    /// <see cref="MaxLength"/>, or carrying control characters — so a caller never has to decide what
    /// an unusable scan means.
    /// </summary>
    public static ParsedIdentifier? Parse(string? scanned)
    {
        if (string.IsNullOrWhiteSpace(scanned)) return null;
        var raw = scanned.Trim();
        if (raw.Length > MaxLength) return null;
        // Control characters reach here from a wedge scanner mid-configuration. Nothing legitimate
        // carries them, and storing them would put unprintable bytes through the audit log.
        if (raw.Any(char.IsControl)) return null;

        var normalised = raw.ToUpperInvariant();

        // Before the prefix reader, not after: "PID: SRW2016-K9 VID: V01" would otherwise match the
        // PID prefix, hand back a remainder carrying a space, and be refused as free text — so the
        // labelled form of the very thing this exists to read would never reach it.
        if (PidVid.Match(normalised) is { Success: true } pidVid
            && Usable(pidVid.Groups["pid"].Value))
        {
            return new ParsedIdentifier(raw, pidVid.Groups["pid"].Value, IdentifierKind.ModelIdentifier);
        }

        if (TryReadPrefix(normalised, out var body, out var declaredKind))
        {
            return Usable(body) ? new ParsedIdentifier(raw, body, declaredKind) : null;
        }

        if (TrySplitCombined(normalised) is { } combined) return combined with { RawValue = raw };


        if (LooksLikeOurLabel(raw)) return new ParsedIdentifier(raw, normalised, IdentifierKind.AssetLabel);

        // No prefix, no known structure. It is a real identifier of *something*, and saying which
        // would be a guess — so it is carried through as Unknown for a person to place.
        return Usable(normalised) ? new ParsedIdentifier(raw, normalised, IdentifierKind.Unknown) : null;
    }

    /// <summary>
    /// The combined Lenovo/IBM label, split into the product identifier it leads with and the device
    /// serial it ends with. Both are returned because both are true, and losing either would mean a
    /// second scan of a barcode already read.
    /// </summary>
    private static ParsedIdentifier? TrySplitCombined(string normalised)
    {
        if (!normalised.StartsWith("1S", StringComparison.Ordinal)) return null;
        var rest = normalised[2..];
        if (rest.Length <= LenovoSerialLength) return null;
        if (!rest.All(char.IsAsciiLetterOrDigit)) return null;

        var model = rest[..^LenovoSerialLength];
        var serial = rest[^LenovoSerialLength..];
        return new ParsedIdentifier(normalised, model, IdentifierKind.ModelIdentifier)
        {
            AlsoCarried = new ParsedIdentifier(normalised, serial, IdentifierKind.SerialNumber),
        };
    }

    private static bool TryReadPrefix(string normalised, out string body, out IdentifierKind kind)
    {
        foreach (var (prefix, declared) in Prefixes)
        {
            if (!normalised.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var after = normalised[prefix.Length..];
            // A separator is required, not optional. Without it "SNXYZ123" — a serial that merely
            // begins with those letters — reads as the prefix SN carrying XYZ123, and the scanned
            // value silently loses its first two characters.
            if (after.Length == 0 || !Separators.Contains(after[0])) continue;
            var remainder = after.TrimStart(Separators);
            if (remainder.Length == 0) continue;
            body = remainder.Trim();
            kind = declared;
            return true;
        }

        body = string.Empty;
        kind = IdentifierKind.Unknown;
        return false;
    }

    /// <summary>Our own printed labels carry an absolute URL, which the CI lookup already reads.</summary>
    private static bool LooksLikeOurLabel(string raw) =>
        Uri.TryCreate(raw, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    private static bool Usable(string value) => value.Length is > 0 and <= MaxLength && Allowed.IsMatch(value);
}
