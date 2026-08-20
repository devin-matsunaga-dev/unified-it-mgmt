namespace Modules.Assets.Features.DeviceIdentification.Dell;

/// <summary>
/// Where Dell's API lives and who we are to it. Every value is configuration and none has a default,
/// which is the point: **the provider is inert until all four are supplied**, so a deployment without
/// a TechDirect account behaves exactly as it did before this existed.
/// <para>
/// The URLs are configuration rather than constants because Dell's documented endpoints are behind
/// their portal and have moved between API versions. Compiling in a URL read from a blog post would
/// be a guess with a deployment behind it; a configured one is a fact somebody checked.
/// </para>
/// <para>
/// <see cref="ClientSecret"/> reaches the host through the same path as every other secret — an
/// Aspire parameter to an environment variable to <c>IConfiguration</c> — and is never prefixed
/// <c>VITE_</c>, which is what would put it in the bundle the browser downloads.
/// </para>
/// </summary>
public sealed class DellOptions
{
    public const string SectionName = "Assets:DeviceIdentification:Dell";

    /// <summary>The OAuth2 token endpoint. Client-credentials grant.</summary>
    public string? TokenUrl { get; set; }

    /// <summary>The asset-entitlements endpoint that takes service tags.</summary>
    public string? AssetEntitlementsUrl { get; set; }

    public string? ClientId { get; set; }

    public string? ClientSecret { get; set; }

    /// <summary>
    /// How long a third party may hold the request open. Ten seconds, matching the webhook channel:
    /// long enough for a slow API, short enough that a hung endpoint cannot make a technician wait
    /// at a receiving desk with a device in their hand.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 10;

    /// <summary>
    /// Renew a token this long before it expires. A token that expires mid-flight costs a retry and
    /// looks like an outage.
    /// </summary>
    public int TokenRenewalMarginSeconds { get; set; } = 60;

    /// <summary>
    /// Whether a successful lookup is written into the product catalogue. Defaults on, because a
    /// mapping reused across a delivery is the point — but it is a switch because whether Dell's
    /// terms permit storing their data is a licensing question, not a technical one, and the answer
    /// may differ per account.
    /// </summary>
    public bool CacheToCatalog { get; set; } = true;

    /// <summary>
    /// Configured means all four of URL, URL, id and secret. A half-configured provider is treated
    /// as absent rather than as an error: a missing secret is a deployment that has not finished,
    /// not a reason to refuse to start.
    /// </summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(TokenUrl)
        && !string.IsNullOrWhiteSpace(AssetEntitlementsUrl)
        && !string.IsNullOrWhiteSpace(ClientId)
        && !string.IsNullOrWhiteSpace(ClientSecret);
}
