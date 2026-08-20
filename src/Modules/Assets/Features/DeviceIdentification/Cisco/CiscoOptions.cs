namespace Modules.Assets.Features.DeviceIdentification.Cisco;

/// <summary>
/// Cisco's Support APIs. Unlike Dell's, the endpoints and the response shape are published openly on
/// DevNet, so these carry the documented values as defaults rather than being left blank — a URL
/// somebody can read in public documentation is a fact, not a guess, and one that has to be typed
/// into configuration before anything works is a deployment step for no benefit.
/// <para>
/// Overridable all the same: Cisco versions these paths, and a customer on a different edition should
/// not need a build to point at theirs.
/// </para>
/// </summary>
public sealed class CiscoOptions
{
    public const string SectionName = "Assets:DeviceIdentification:Cisco";

    /// <summary>Cisco Common Identity SSO, client-credentials grant.</summary>
    public string TokenUrl { get; set; } = "https://id.cisco.com/oauth2/default/v1/token";

    /// <summary>
    /// Coverage summary by serial. The serial goes on the path rather than in the query, which is why
    /// this is a base the provider appends an escaped segment to and never a format string.
    /// </summary>
    public string CoverageSummaryUrl { get; set; } =
        "https://apix.cisco.com/sn2info/v2/coverage/summary/serial_numbers";

    public string? ClientId { get; set; }

    public string? ClientSecret { get; set; }

    public int TimeoutSeconds { get; set; } = 10;

    public int TokenRenewalMarginSeconds { get; set; } = 60;

    /// <summary>Whether a successful lookup is written into the product catalogue.</summary>
    public bool CacheToCatalog { get; set; } = true;

    /// <summary>
    /// Only the credentials decide this, because the URLs already have documented defaults. Access is
    /// gated on being an SNTC customer or a PSS partner, so most deployments will never set them and
    /// this provider will never run.
    /// </summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ClientId)
        && !string.IsNullOrWhiteSpace(ClientSecret)
        && !string.IsNullOrWhiteSpace(TokenUrl)
        && !string.IsNullOrWhiteSpace(CoverageSummaryUrl);
}
