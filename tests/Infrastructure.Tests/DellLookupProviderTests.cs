using System.Net;
using System.Text;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Modules.Assets.Data;
using Modules.Assets.Features.DeviceIdentification;
using Modules.Assets.Features.DeviceIdentification.Dell;

namespace Infrastructure.Tests;

/// <summary>
/// The Dell transport, against a stubbed handler. Everything here is about what happens when the
/// third party misbehaves — because the one guarantee this provider owes the rest of the system is
/// that nothing Dell does can stop a device being registered.
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class DellLookupProviderTests : IAsyncLifetime
{
    private readonly string _connectionString;
    private AssetsDbContext _dbContext = null!;

    public DellLookupProviderTests(InfrastructureFixture infrastructure) =>
        _connectionString = infrastructure.PostgresConnectionString;

    public async Task InitializeAsync()
    {
        _dbContext = new AssetsDbContext(new DbContextOptionsBuilder<AssetsDbContext>()
            .UseNpgsql(_connectionString).Options);
        await _dbContext.Database.MigrateAsync();
    }

    public async Task DisposeAsync() => await _dbContext.DisposeAsync();

    private static readonly IdentifierView[] ServiceTag =
        [new("Service Tag: 7XKLM92", "7XKLM92", IdentifierKind.SerialNumber)];

    /// <summary>Without a TechDirect account this must cost nothing and reach nobody.</summary>
    [Fact]
    public async Task Lookup_WhenUnconfigured_AsksNobody()
    {
        var handler = new StubHandler();
        var provider = Build(handler, new DellOptions());

        var result = await provider.LookupAsync(ServiceTag, CancellationToken.None);

        Assert.Equal(IdentificationConfidence.Unknown, result.Confidence);
        Assert.Empty(handler.Requests);
    }

    /// <summary>The API is keyed per device; a product identifier is not something it answers about.</summary>
    [Fact]
    public async Task Lookup_WithOnlyAProductIdentifier_AsksNobody()
    {
        var handler = new StubHandler();
        var provider = Build(handler, Configured());

        var result = await provider.LookupAsync(
            [new("P/N: 12RQ000KUS", "12RQ000KUS", IdentifierKind.ModelIdentifier)], CancellationToken.None);

        Assert.Equal(IdentificationConfidence.Unknown, result.Confidence);
        Assert.Empty(handler.Requests);
    }

    /// <summary>
    /// The gap this closed: most manufacturers print a bare alphanumeric, which the parser refuses to
    /// call a serial because it will not *store* a guess. Asking is a different act — the vendor
    /// either recognises the string or does not, and its answer is authoritative.
    /// </summary>
    [Fact]
    public async Task Lookup_WithAnUnclassifiedScan_StillAsksTheManufacturer()
    {
        var tag = $"FDO{Guid.NewGuid():N}"[..11].ToUpperInvariant();
        var handler = new StubHandler();
        var provider = Build(handler, Configured(), new StubMapper(Answer()));

        var result = await provider.LookupAsync(
            [new(tag, tag, IdentifierKind.Unknown)], CancellationToken.None);

        Assert.Equal("Dell", result.Source);
        Assert.Contains(handler.Requests, uri => uri.Contains(tag, StringComparison.Ordinal));
    }

    /// <summary>
    /// A classified serial is asked about before an unclassified string, so a labelled barcode is
    /// never passed over in favour of a shipping reference that happened to be scanned first.
    /// </summary>
    [Fact]
    public async Task Lookup_PrefersAClassifiedSerialOverAnUnclassifiedScan()
    {
        var handler = new StubHandler();
        var provider = Build(handler, Configured(), new StubMapper(Answer()));

        await provider.LookupAsync(
            [
                new("SHIP4402", "SHIP4402", IdentifierKind.Unknown),
                new("S/N: 7XKLM92", "7XKLM92", IdentifierKind.SerialNumber),
            ],
            CancellationToken.None);

        var asked = handler.Requests.First(uri => !uri.Contains("/token", StringComparison.Ordinal));
        Assert.Contains("7XKLM92", asked, StringComparison.Ordinal);
    }

    /// <summary>Bounded: a scanner that read a whole label must not become a burst of API calls.</summary>
    [Fact]
    public async Task Lookup_WithManyUnclassifiedScans_AsksAboutAtMostThree()
    {
        var handler = new StubHandler();
        var provider = Build(handler, Configured(), new StubMapper(null));

        await provider.LookupAsync(
            [.. Enumerable.Range(0, 8).Select(index =>
                new IdentifierView($"CODE{index}", $"CODE{index}", IdentifierKind.Unknown))],
            CancellationToken.None);

        Assert.Equal(3, handler.Requests.Count(uri => !uri.Contains("/token", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Lookup_TakesATokenOnceAndReusesIt()
    {
        var handler = new StubHandler();
        var provider = Build(handler, Configured(), new StubMapper(Answer()));

        await provider.LookupAsync(ServiceTag, CancellationToken.None);
        await provider.LookupAsync(
            [new("Service Tag: OTHER99", "OTHER99", IdentifierKind.SerialNumber)], CancellationToken.None);

        Assert.Equal(1, handler.Requests.Count(uri => uri.Contains("/token", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Lookup_SendsTheServiceTagAsAnEscapedQueryValue()
    {
        var handler = new StubHandler();
        var provider = Build(handler, Configured(), new StubMapper(Answer()));

        await provider.LookupAsync(ServiceTag, CancellationToken.None);

        Assert.Contains(handler.Requests, uri => uri.Contains("servicetags=7XKLM92", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Lookup_WhenRateLimited_ReturnsNothingRatherThanThrowing()
    {
        var handler = new StubHandler { AssetStatus = HttpStatusCode.TooManyRequests };
        var provider = Build(handler, Configured(), new StubMapper(Answer()));

        var result = await provider.LookupAsync(ServiceTag, CancellationToken.None);

        Assert.Equal(IdentificationConfidence.Unknown, result.Confidence);
    }

    [Fact]
    public async Task Lookup_WhenDellIsUnreachable_ReturnsNothingRatherThanThrowing()
    {
        var handler = new StubHandler { Throw = new HttpRequestException("No route to host.") };
        var provider = Build(handler, Configured(), new StubMapper(Answer()));

        var result = await provider.LookupAsync(ServiceTag, CancellationToken.None);

        Assert.Equal(IdentificationConfidence.Unknown, result.Confidence);
    }

    /// <summary>A hung endpoint must not hold a technician at a receiving desk.</summary>
    [Fact]
    public async Task Lookup_WhenDellDoesNotAnswerInTime_ReturnsNothing()
    {
        var handler = new StubHandler { Delay = TimeSpan.FromSeconds(5) };
        var provider = Build(handler, Configured(timeoutSeconds: 1), new StubMapper(Answer()));

        var result = await provider.LookupAsync(ServiceTag, CancellationToken.None);

        Assert.Equal(IdentificationConfidence.Unknown, result.Confidence);
    }

    /// <summary>
    /// The answer is remembered twice, for two questions: the memo so this machine is never asked
    /// about again, the catalogue so its product is known without an API call.
    /// </summary>
    [Fact]
    public async Task Lookup_OnSuccess_RemembersTheDeviceAndItsProduct()
    {
        var tag = $"SVC{Guid.NewGuid():N}"[..12].ToUpperInvariant();
        var product = $"PRD{Guid.NewGuid():N}"[..12].ToUpperInvariant();
        var handler = new StubHandler();
        var provider = Build(handler, Configured(), new StubMapper(Answer(product)));

        var result = await provider.LookupAsync(
            [new(tag, tag, IdentifierKind.SerialNumber)], CancellationToken.None);

        Assert.Equal("Dell", result.Source);
        Assert.Equal(tag, result.SerialNumber);

        Assert.NotNull(await _dbContext.DeviceLookupMemos.AsNoTracking()
            .FirstOrDefaultAsync(memo => memo.Identifier == tag));
        var entry = await _dbContext.ProductCatalogEntries.AsNoTracking()
            .FirstOrDefaultAsync(item => item.ModelIdentifier == product);
        Assert.NotNull(entry);
        // The provenance survives the caching — a prefill can still say Dell said so, not a person.
        Assert.Equal(ProductCatalogSource.Dell, entry.Source);
    }

    /// <summary>
    /// A device Dell named but gave no product identifier for. Nothing may be written to the
    /// catalogue: there is no key a later device could match on, and inventing one from the service
    /// tag is exactly what the catalogue exists to prevent.
    /// </summary>
    [Fact]
    public async Task Lookup_WithNoProductIdentifier_RemembersTheDeviceButNotAProduct()
    {
        var tag = $"SVC{Guid.NewGuid():N}"[..12].ToUpperInvariant();
        var before = await _dbContext.ProductCatalogEntries.CountAsync();
        var handler = new StubHandler();
        var provider = Build(handler, Configured(), new StubMapper(new DeviceIdentificationResult
        {
            Manufacturer = "Dell", Model = "Latitude 5450", Confidence = IdentificationConfidence.High,
        }));

        await provider.LookupAsync([new(tag, tag, IdentifierKind.SerialNumber)], CancellationToken.None);

        Assert.NotNull(await _dbContext.DeviceLookupMemos.AsNoTracking()
            .FirstOrDefaultAsync(memo => memo.Identifier == tag));
        Assert.Equal(before, await _dbContext.ProductCatalogEntries.CountAsync());
    }

    /// <summary>
    /// The shipped mapper answers nothing until it is written against a real Dell response. This
    /// asserts the state is deliberate rather than a half-finished class: a configured provider with
    /// a live API still identifies nothing, and nothing is cached from an answer it cannot read.
    /// </summary>
    [Fact]
    public async Task Lookup_WithTheShippedMapper_IdentifiesNothingYet()
    {
        // Its own tag: the shared one has a memo written by another case in this class, and the
        // assertion here is about what this lookup did, not what the database already held.
        var tag = $"SVC{Guid.NewGuid():N}"[..12].ToUpperInvariant();
        var handler = new StubHandler();
        var provider = Build(handler, Configured(), new DellEntitlementMapper());

        var result = await provider.LookupAsync(
            [new(tag, tag, IdentifierKind.SerialNumber)], CancellationToken.None);

        Assert.Equal(IdentificationConfidence.Unknown, result.Confidence);
        Assert.Empty(await _dbContext.DeviceLookupMemos.AsNoTracking()
            .Where(memo => memo.Identifier == tag).ToListAsync());
    }

    private static DeviceIdentificationResult Answer(string? productNumber = "12RQ000KUS") => new()
    {
        Manufacturer = "Dell",
        Model = "Latitude 5450",
        ProductNumber = productNumber,
        DeviceType = "Laptop",
        Confidence = IdentificationConfidence.High,
    };

    private static DellOptions Configured(int timeoutSeconds = 10) => new()
    {
        TokenUrl = "https://dell.example.test/token",
        AssetEntitlementsUrl = "https://dell.example.test/asset-entitlements",
        ClientId = "test-client",
        ClientSecret = "test-secret",
        TimeoutSeconds = timeoutSeconds,
    };

    private DellLookupProvider Build(
        StubHandler handler, DellOptions dellOptions, IDellEntitlementMapper? mapper = null) =>
        // A fresh cache per provider, so no test case inherits a token another one fetched.
        new(Options.Create(dellOptions), new StubHttpClientFactory(handler),
            mapper ?? new StubMapper(null), new OAuthTokenCache(), _dbContext,
            NullLogger<DellLookupProvider>.Instance);

    private sealed class StubMapper(DeviceIdentificationResult? result) : IDellEntitlementMapper
    {
        public DeviceIdentificationResult? Map(JsonElement body, string serviceTag) => result;
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        public HttpStatusCode AssetStatus { get; init; } = HttpStatusCode.OK;
        public Exception? Throw { get; init; }
        public TimeSpan Delay { get; init; } = TimeSpan.Zero;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.ToString());
            var isToken = request.RequestUri!.AbsolutePath.Contains("token", StringComparison.Ordinal);
            if (Throw is not null && !isToken) throw Throw;
            if (Delay > TimeSpan.Zero && !isToken) await Task.Delay(Delay, cancellationToken);

            var json = isToken
                ? """{"access_token":"stub-token","expires_in":3600}"""
                : """{"stub":true}""";
            return new HttpResponseMessage(isToken ? HttpStatusCode.OK : AssetStatus)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        }
    }
}
