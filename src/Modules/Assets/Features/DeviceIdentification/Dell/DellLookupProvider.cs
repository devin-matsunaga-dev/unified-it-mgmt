using System.Text.Json;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Modules.Assets.Data;

namespace Modules.Assets.Features.DeviceIdentification.Dell;

/// <summary>
/// Dell's asset-entitlements API, asked only about a device nothing local could identify.
/// <para>
/// It runs after the product catalogue and the remembered lookup, so a third party is asked once per
/// device that has never been seen and never again. Everything about how that request is made,
/// timed out and remembered lives in <see cref="ExternalLookupProvider"/>; what is Dell's is the URL
/// shape and the mapper.
/// </para>
/// <para>
/// **Inert unless configured**, which is why it can ship before a TechDirect account exists.
/// </para>
/// </summary>
public sealed class DellLookupProvider(
    IOptions<DellOptions> options,
    IHttpClientFactory httpClientFactory,
    IDellEntitlementMapper mapper,
    OAuthTokenCache tokenCache,
    AssetsDbContext dbContext,
    ILogger<DellLookupProvider> logger)
    : ExternalLookupProvider(httpClientFactory, tokenCache, dbContext, logger)
{
    private readonly DellOptions _options = options.Value;

    public override int Order => 10;

    public override string Name => "Dell";

    protected override bool IsConfigured => _options.IsConfigured;

    protected override TimeSpan Timeout => TimeSpan.FromSeconds(_options.TimeoutSeconds);

    protected override TimeSpan TokenRenewalMargin =>
        TimeSpan.FromSeconds(_options.TokenRenewalMarginSeconds);

    protected override bool CacheToCatalog => _options.CacheToCatalog;

    protected override ProductCatalogSource CatalogSource => ProductCatalogSource.Dell;

    protected override (string Url, string ClientId, string ClientSecret) TokenRequest =>
        (_options.TokenUrl!, _options.ClientId!, _options.ClientSecret!);

    /// <summary>
    /// The service tag goes in as an escaped query value on a URL that came from configuration —
    /// never concatenated from anything a caller supplied.
    /// </summary>
    protected override Uri BuildLookupUri(string serialNumber)
    {
        var uri = new UriBuilder(_options.AssetEntitlementsUrl!);
        var existing = string.IsNullOrEmpty(uri.Query) ? string.Empty : uri.Query.TrimStart('?') + "&";
        uri.Query = existing + "servicetags=" + Uri.EscapeDataString(serialNumber);
        return uri.Uri;
    }

    protected override DeviceIdentificationResult? Map(JsonElement body, string serialNumber) =>
        mapper.Map(body, serialNumber);
}
