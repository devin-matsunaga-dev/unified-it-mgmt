namespace Modules.Assets.Features.Labels;

/// <summary>
/// What a label's QR carries, and how a scanned code is read back. The payload is an absolute URL so
/// a phone camera opens the asset page directly with nothing in between; every other form a scanner
/// can produce — a bare id, an asset tag, a serial number — is resolved against the database instead.
/// </summary>
public static class CiLabelCodes
{
    /// <summary>
    /// The address the printed QR points at. It has to be reachable from the device doing the
    /// scanning, so "localhost" only ever works on the developer's own machine.
    /// </summary>
    public const string PublicBaseUrlKey = "Assets:Labels:PublicBaseUrl";

    /// <summary>The CORS origin doubles as the base URL when nothing more specific is configured.</summary>
    public const string WebClientOriginKey = "WebClient:Origin";

    public const string DefaultBaseUrl = "http://localhost:5173";

    public static string PayloadFor(string? baseUrl, Guid ciId) => $"{BaseUrl(baseUrl)}/assets/{ciId}";

    public static string BaseUrl(string? configured) =>
        string.IsNullOrWhiteSpace(configured) ? DefaultBaseUrl : configured.Trim().TrimEnd('/');

    /// <summary>
    /// Reads a CI id out of a scanned code: one of our own label URLs, any URL whose last segment is
    /// an id, or the bare id someone pasted. Anything else is an identifier only the database can
    /// resolve, and the caller falls back to matching a serial number or an asset tag.
    /// </summary>
    public static bool TryReadCiId(string? code, out Guid ciId)
    {
        ciId = Guid.Empty;
        var trimmed = code?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return false;
        }

        if (Guid.TryParse(trimmed, out ciId))
        {
            return true;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
        {
            return false;
        }

        var lastSegment = uri.Segments.Length == 0 ? string.Empty : uri.Segments[^1].TrimEnd('/');
        return Guid.TryParse(lastSegment, out ciId);
    }
}
