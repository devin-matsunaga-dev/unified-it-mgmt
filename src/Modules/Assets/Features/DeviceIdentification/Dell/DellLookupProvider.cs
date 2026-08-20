using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Modules.Assets.Data;

namespace Modules.Assets.Features.DeviceIdentification.Dell;

/// <summary>
/// Dell's asset-entitlements API, asked only about a device nothing local could identify.
/// <para>
/// It runs last on purpose. The product catalogue answers first, then a remembered lookup for this
/// exact device — so a third party is asked once per device that has never been seen, and never
/// again. A successful answer is written to both: the memo so this machine is never re-queried, and
/// the catalogue so the product it belongs to is known even without an API call.
/// </para>
/// <para>
/// **Inert unless configured.** Without a TechDirect account this provider returns nothing and costs
/// one boolean, which is why it can ship before the account exists.
/// </para>
/// </summary>
public sealed class DellLookupProvider(
    IOptions<DellOptions> options,
    IHttpClientFactory httpClientFactory,
    IDellEntitlementMapper mapper,
    DellTokenCache tokenCache,
    AssetsDbContext dbContext,
    ILogger<DellLookupProvider> logger) : IDeviceLookupProvider
{
    private readonly DellOptions _options = options.Value;

    public int Order => 10;

    public string Name => "Dell";

    public async Task<DeviceIdentificationResult> LookupAsync(
        IReadOnlyList<IdentifierView> identifiers,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identifiers);
        if (!_options.IsConfigured) return DeviceIdentificationResult.None;

        // A service tag is what Dell answers about. Anything the parser called a serial is a
        // candidate; a product identifier is not, because this API is keyed per device.
        var serviceTag = identifiers
            .FirstOrDefault(identifier => identifier.Kind == IdentifierKind.SerialNumber)?.Value;
        if (serviceTag is null) return DeviceIdentificationResult.None;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

        try
        {
            var token = await GetTokenAsync(timeout.Token);
            if (token is null) return DeviceIdentificationResult.None;

            var client = httpClientFactory.CreateClient();
            // The service tag goes in as a query value on a URL that came from configuration — never
            // concatenated into a URL a caller supplied, and escaped either way.
            var uri = new UriBuilder(_options.AssetEntitlementsUrl!);
            var query = string.IsNullOrEmpty(uri.Query) ? string.Empty : uri.Query.TrimStart('?') + "&";
            uri.Query = query + "servicetags=" + Uri.EscapeDataString(serviceTag);

            using var request = new HttpRequestMessage(HttpMethod.Get, uri.Uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await client.SendAsync(request, timeout.Token);

            if (response.StatusCode is HttpStatusCode.TooManyRequests)
            {
                // Rate limited. A soft failure: the device is simply unidentified this time, and the
                // technician registers it by hand rather than waiting on a retry loop at a desk.
                logger.LogWarning(
                    "Dell rate-limited an asset lookup. Retry-After: {RetryAfter}.",
                    response.Headers.RetryAfter?.ToString() ?? "not given");
                return DeviceIdentificationResult.None;
            }

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Dell asset lookup answered {Status}.", (int)response.StatusCode);
                return DeviceIdentificationResult.None;
            }

            using var body = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(timeout.Token), cancellationToken: timeout.Token);
            var result = mapper.Map(body.RootElement, serviceTag);
            if (result is null) return DeviceIdentificationResult.None;

            await RememberAsync(serviceTag, result, timeout.Token);
            return result with { Source = Name, SerialNumber = serviceTag };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                "Dell asset lookup did not answer within {Seconds}s.", _options.TimeoutSeconds);
            return DeviceIdentificationResult.None;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Nothing Dell does may stop a device being registered, so every failure is the same
            // failure: not identified. The exception is logged; the credentials are not in it.
            logger.LogWarning(exception, "Dell asset lookup failed.");
            return DeviceIdentificationResult.None;
        }
    }

    /// <summary>
    /// Records the answer twice, for two different questions. The memo means this machine is never
    /// asked about again; the catalogue means the product it belongs to is known without an API call,
    /// which is what makes the second device of a delivery free rather than merely cheaper.
    /// </summary>
    private async Task RememberAsync(
        string serviceTag, DeviceIdentificationResult result, CancellationToken cancellationToken)
    {
        if (!_options.CacheToCatalog) return;

        var now = DateTimeOffset.UtcNow;
        var existing = await dbContext.DeviceLookupMemos
            .FirstOrDefaultAsync(memo => memo.Identifier == serviceTag, cancellationToken);
        if (existing is null)
        {
            dbContext.DeviceLookupMemos.Add(new DeviceLookupMemo
            {
                Id = Guid.CreateVersion7(),
                Identifier = serviceTag,
                Manufacturer = result.Manufacturer,
                Model = result.Model,
                ProductNumber = result.ProductNumber,
                DeviceType = result.DeviceType,
                Source = Name,
                FetchedAt = now,
            });
        }

        // Only when Dell named the product. Without a product identifier there is no key a later
        // device could match on, and inventing one from the service tag is the exact mistake the
        // catalogue exists to avoid.
        if (result.ProductNumber is { Length: > 0 } productNumber
            && result.Manufacturer is { Length: > 0 }
            && result.Model is { Length: > 0 })
        {
            var entry = await dbContext.ProductCatalogEntries
                .FirstOrDefaultAsync(item => item.ModelIdentifier == productNumber, cancellationToken);
            if (entry is null)
            {
                dbContext.ProductCatalogEntries.Add(new ProductCatalogEntry
                {
                    Id = Guid.CreateVersion7(),
                    ModelIdentifier = productNumber,
                    Manufacturer = result.Manufacturer,
                    Model = result.Model,
                    ProductNumber = productNumber,
                    DeviceType = result.DeviceType,
                    // The provenance survives the caching, so a prefill can still say Dell said so
                    // rather than a person did.
                    Source = ProductCatalogSource.Dell,
                    CreatedBy = Name,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// A token from the shared cache, fetching one only when it has none that will outlive the call.
    /// The client-credentials grant is standard OAuth2; only the URL is Dell's, and that is
    /// configuration.
    /// </summary>
    private Task<string?> GetTokenAsync(CancellationToken cancellationToken) =>
        tokenCache.GetAsync(
            TimeSpan.FromSeconds(_options.TokenRenewalMarginSeconds),
            async token =>
            {
                var client = httpClientFactory.CreateClient();
                using var request = new HttpRequestMessage(HttpMethod.Post, _options.TokenUrl);
                request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials",
                    ["client_id"] = _options.ClientId!,
                    ["client_secret"] = _options.ClientSecret!,
                });

                using var response = await client.SendAsync(request, token);
                if (!response.IsSuccessStatusCode)
                {
                    // The status only. A body from a failed token request can echo what was sent.
                    logger.LogWarning("Dell token request answered {Status}.", (int)response.StatusCode);
                    return null;
                }

                using var body = await JsonDocument.ParseAsync(
                    await response.Content.ReadAsStreamAsync(token), cancellationToken: token);
                if (!body.RootElement.TryGetProperty("access_token", out var accessToken)
                    || accessToken.GetString() is not { Length: > 0 } value)
                {
                    logger.LogWarning("Dell token response carried no access_token.");
                    return null;
                }

                var lifetime = body.RootElement.TryGetProperty("expires_in", out var expiresIn)
                    && expiresIn.TryGetInt32(out var seconds)
                        ? TimeSpan.FromSeconds(seconds)
                        // Dell documents about an hour; assumed conservatively when unstated, because
                        // the cost of assuming too long is a request that fails for no visible reason.
                        : TimeSpan.FromMinutes(30);
                return (value, lifetime);
            },
            cancellationToken);

}
