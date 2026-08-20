using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using Modules.Assets.Data;

namespace Modules.Assets.Features.DeviceIdentification;

/// <summary>
/// What every manufacturer lookup has in common: take an OAuth2 client-credentials token, ask about
/// one device before a timeout, and remember the answer so nobody is asked twice.
/// <para>
/// A base class rather than two copies because the parts that differ between manufacturers are
/// small — a URL shape and a response mapper — and the parts that must not differ are the ones that
/// matter: **every failure becomes "not identified", never an exception**, and a serial is never
/// written to the product catalogue as if it named a product. A second copy of that reasoning is a
/// second place for it to be got wrong.
/// </para>
/// </summary>
public abstract class ExternalLookupProvider(
    IHttpClientFactory httpClientFactory,
    OAuthTokenCache tokenCache,
    AssetsDbContext dbContext,
    ILogger logger) : IDeviceLookupProvider
{
    public abstract int Order { get; }

    public abstract string Name { get; }

    /// <summary>False when the manufacturer has no credentials, which makes the provider inert.</summary>
    protected abstract bool IsConfigured { get; }

    protected abstract TimeSpan Timeout { get; }

    protected abstract TimeSpan TokenRenewalMargin { get; }

    protected abstract bool CacheToCatalog { get; }

    /// <summary>Where a token comes from and who we are.</summary>
    protected abstract (string Url, string ClientId, string ClientSecret) TokenRequest { get; }

    /// <summary>The request that asks about one device. Built per manufacturer.</summary>
    protected abstract Uri BuildLookupUri(string serialNumber);

    protected abstract DeviceIdentificationResult? Map(JsonElement body, string serialNumber);

    public async Task<DeviceIdentificationResult> LookupAsync(
        IReadOnlyList<IdentifierView> identifiers,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identifiers);
        if (!IsConfigured) return DeviceIdentificationResult.None;

        var candidates = SerialCandidates(identifiers);
        if (candidates.Count == 0) return DeviceIdentificationResult.None;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);

        try
        {
            var token = await GetTokenAsync(timeout.Token);
            if (token is null) return DeviceIdentificationResult.None;

            foreach (var candidate in candidates)
            {
                var found = await AskAsync(token, candidate, timeout.Token);
                if (found is not null) return found;
            }

            return DeviceIdentificationResult.None;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                "{Provider} device lookup did not answer within {Seconds}s.", Name, Timeout.TotalSeconds);
            return DeviceIdentificationResult.None;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Nothing a manufacturer does may stop a device being registered, so every failure is the
            // same failure: not identified. The exception is logged; the credentials are not in it.
            logger.LogWarning(exception, "{Provider} device lookup failed.", Name);
            return DeviceIdentificationResult.None;
        }
    }

    /// <summary>
    /// How many unclassified scans may be tried against a manufacturer. A device wears a handful of
    /// barcodes and only one is its serial; trying every string a scanner produced would multiply
    /// calls and spend rate limit on shipping references.
    /// </summary>
    private const int MaxCandidates = 3;

    /// <summary>
    /// What to ask the manufacturer about, best first.
    /// <para>
    /// Anything the parser called a serial leads. **Then the unclassified ones** — because a bare
    /// alphanumeric is exactly what most manufacturers print, and the parser refuses to call it a
    /// serial for a good reason: it will not *store* a guess. Asking is a different act from storing.
    /// The vendor either recognises the string or does not, and its answer is authoritative where a
    /// local guess never could be.
    /// </para>
    /// <para>
    /// A product identifier is never offered: these APIs are keyed per device and would not know it.
    /// </para>
    /// </summary>
    private static List<string> SerialCandidates(IReadOnlyList<IdentifierView> identifiers) =>
    [
        .. identifiers.Where(identifier => identifier.Kind == IdentifierKind.SerialNumber)
            .Select(identifier => identifier.Value)
            .Concat(identifiers.Where(identifier => identifier.Kind == IdentifierKind.Unknown)
                .Select(identifier => identifier.Value))
            .Distinct(StringComparer.Ordinal)
            .Take(MaxCandidates),
    ];

    private async Task<DeviceIdentificationResult?> AskAsync(
        string token, string serialNumber, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, BuildLookupUri(serialNumber));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await client.SendAsync(request, cancellationToken);

        if (response.StatusCode is HttpStatusCode.TooManyRequests)
        {
            // A soft failure. The device is unidentified this time and the technician registers
            // it by hand, rather than waiting at a receiving desk on a retry loop.
            logger.LogWarning(
                "{Provider} rate-limited a device lookup. Retry-After: {RetryAfter}.",
                Name, response.Headers.RetryAfter?.ToString() ?? "not given");
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("{Provider} device lookup answered {Status}.", Name, (int)response.StatusCode);
            return null;
        }

        using var body = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var result = Map(body.RootElement, serialNumber);
        if (result is null) return null;

        await RememberAsync(serialNumber, result, cancellationToken);
        return result with { Source = Name, SerialNumber = serialNumber };
    }

    /// <summary>
    /// Records the answer twice, for two different questions. The memo means this machine is never
    /// asked about again; the catalogue means the product it belongs to is known without an API call,
    /// which is what makes the second device of a delivery free rather than merely cheaper.
    /// </summary>
    private async Task RememberAsync(
        string serialNumber, DeviceIdentificationResult result, CancellationToken cancellationToken)
    {
        if (!CacheToCatalog) return;

        var now = DateTimeOffset.UtcNow;
        var existing = await dbContext.DeviceLookupMemos
            .FirstOrDefaultAsync(memo => memo.Identifier == serialNumber, cancellationToken);
        if (existing is null)
        {
            dbContext.DeviceLookupMemos.Add(new DeviceLookupMemo
            {
                Id = Guid.CreateVersion7(),
                Identifier = serialNumber,
                Manufacturer = result.Manufacturer,
                Model = result.Model,
                ProductNumber = result.ProductNumber,
                DeviceType = result.DeviceType,
                Source = Name,
                FetchedAt = now,
            });
        }

        // Only when the manufacturer named the product. Without a product identifier there is no key
        // a later device could match on, and inventing one from the serial is the exact mistake the
        // catalogue exists to avoid.
        if (result.ProductNumber is { Length: > 0 } productNumber
            && result.Manufacturer is { Length: > 0 } manufacturer
            && result.Model is { Length: > 0 } model)
        {
            var entry = await dbContext.ProductCatalogEntries
                .FirstOrDefaultAsync(item => item.ModelIdentifier == productNumber, cancellationToken);
            if (entry is null)
            {
                dbContext.ProductCatalogEntries.Add(new ProductCatalogEntry
                {
                    Id = Guid.CreateVersion7(),
                    ModelIdentifier = productNumber,
                    Manufacturer = manufacturer,
                    Model = model,
                    ProductNumber = productNumber,
                    DeviceType = result.DeviceType,
                    // The provenance survives the caching, so a prefill can still say a manufacturer
                    // answered rather than a person typed it.
                    Source = CatalogSource,
                    CreatedBy = Name,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    protected abstract ProductCatalogSource CatalogSource { get; }

    /// <summary>
    /// A token from the shared cache, fetching one only when none will outlive the call. The
    /// client-credentials grant is standard OAuth2; only the URL is the manufacturer's.
    /// </summary>
    private Task<string?> GetTokenAsync(CancellationToken cancellationToken) =>
        tokenCache.GetAsync(Name, TokenRenewalMargin, async token =>
        {
            var (url, clientId, clientSecret) = TokenRequest;
            var client = httpClientFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
            });

            using var response = await client.SendAsync(request, token);
            if (!response.IsSuccessStatusCode)
            {
                // The status only. A body from a failed token request can echo what was sent.
                logger.LogWarning("{Provider} token request answered {Status}.", Name, (int)response.StatusCode);
                return null;
            }

            using var body = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(token), cancellationToken: token);
            if (!body.RootElement.TryGetProperty("access_token", out var accessToken)
                || accessToken.GetString() is not { Length: > 0 } value)
            {
                logger.LogWarning("{Provider} token response carried no access_token.", Name);
                return null;
            }

            var lifetime = body.RootElement.TryGetProperty("expires_in", out var expiresIn)
                && expiresIn.TryGetInt32(out var seconds)
                    ? TimeSpan.FromSeconds(seconds)
                    // Assumed conservatively when unstated: the cost of assuming too long is a
                    // request that fails for no visible reason.
                    : TimeSpan.FromMinutes(30);
            return (value, lifetime);
        }, cancellationToken);
}
