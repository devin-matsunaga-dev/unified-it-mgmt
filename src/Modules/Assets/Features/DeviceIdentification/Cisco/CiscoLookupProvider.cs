using System.Text.Json;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Modules.Assets.Data;

namespace Modules.Assets.Features.DeviceIdentification.Cisco;

/// <summary>
/// Cisco's SN2INFO coverage API. Structurally the same as Dell's — token, ask about one serial,
/// remember the answer — and different in two ways only: the serial goes on the path rather than in
/// a query, and the response shape is public, so the mapper could be written from documentation
/// instead of waiting for a sample.
/// <para>
/// **Inert unless credentials are configured.** Access is gated on being an SNTC customer or a PSS
/// partner, so most deployments will never set them and this will never run.
/// </para>
/// </summary>
public sealed class CiscoLookupProvider(
    IOptions<CiscoOptions> options,
    IHttpClientFactory httpClientFactory,
    ICiscoCoverageMapper mapper,
    OAuthTokenCache tokenCache,
    AssetsDbContext dbContext,
    ILogger<CiscoLookupProvider> logger)
    : ExternalLookupProvider(httpClientFactory, tokenCache, dbContext, logger)
{
    private readonly CiscoOptions _options = options.Value;

    /// <summary>After Dell: a device carries one manufacturer, and whichever answers first is right.</summary>
    public override int Order => 20;

    public override string Name => "Cisco";

    protected override bool IsConfigured => _options.IsConfigured;

    protected override TimeSpan Timeout => TimeSpan.FromSeconds(_options.TimeoutSeconds);

    protected override TimeSpan TokenRenewalMargin =>
        TimeSpan.FromSeconds(_options.TokenRenewalMarginSeconds);

    protected override bool CacheToCatalog => _options.CacheToCatalog;

    protected override ProductCatalogSource CatalogSource => ProductCatalogSource.Cisco;

    protected override (string Url, string ClientId, string ClientSecret) TokenRequest =>
        (_options.TokenUrl, _options.ClientId!, _options.ClientSecret!);

    /// <summary>
    /// The serial is a path segment here, not a query value — so it is escaped as one. A serial
    /// carrying a slash would otherwise change which endpoint was called rather than which device
    /// was asked about.
    /// </summary>
    protected override Uri BuildLookupUri(string serialNumber) =>
        new($"{_options.CoverageSummaryUrl.TrimEnd('/')}/{Uri.EscapeDataString(serialNumber)}");

    protected override DeviceIdentificationResult? Map(JsonElement body, string serialNumber) =>
        mapper.Map(body, serialNumber);
}
