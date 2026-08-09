using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Modules.Assets.Data;
using Modules.Assets.Features.Labels;
using Platform.Data;

namespace Infrastructure.Tests;

/// <summary>
/// WP-2.7 end to end: a single label, a batch sheet, and the scan lookup resolving every code a
/// scanner can produce back to its CI.
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class CiLabelApiIntegrationTests : IAsyncLifetime
{
    private const string BaseUrl = "http://192.168.1.20:5173";

    private readonly LabelApplication _application;
    private HttpClient? _client;

    public CiLabelApiIntegrationTests(InfrastructureFixture infrastructure)
    {
        _application = new LabelApplication(
            infrastructure.PostgresConnectionString,
            infrastructure.RabbitMqConnectionString,
            infrastructure.MinioConnectionString);
    }

    public async Task InitializeAsync()
    {
        _client = _application.CreateClient();
        await using var scope = _application.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<PlatformDbContext>().Database.MigrateAsync();
        await scope.ServiceProvider.GetRequiredService<AssetsDbContext>().Database.MigrateAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task SingleLabel_IsAPdfNamedAfterTheAssetTag()
    {
        var ci = await CreateCiAsync("Hardware", "Reception laptop", assetTag: "LT-00421");

        using var response = await _client!.SendAsync(Authenticated(HttpMethod.Get, $"/api/cis/{ci.Id}/label"));
        var content = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("%PDF", Encoding.ASCII.GetString(content, 0, 4));
        Assert.Contains("lt-00421", response.Content.Headers.ContentDisposition?.FileName ?? "", StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Standard")]
    [InlineData("Small")]
    public async Task SingleLabel_IsPrintableInEitherSize(string size)
    {
        var ci = await CreateCiAsync("Hardware", $"Laptop for the {size} sheet");

        using var response = await _client!.SendAsync(
            Authenticated(HttpMethod.Get, $"/api/cis/{ci.Id}/label?size={size}"));
        var content = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("%PDF", Encoding.ASCII.GetString(content, 0, 4));
    }

    [Fact]
    public async Task LabelSheet_ForSeveralCis_IsOnePdf()
    {
        var first = await CreateCiAsync("Hardware", "Batch laptop one", assetTag: $"LT-{Guid.NewGuid():N}"[..10]);
        var second = await CreateCiAsync("Server", "Batch server two");
        var third = await CreateCiAsync("Hardware", "Batch laptop three");

        using var request = Authenticated(HttpMethod.Post, "/api/ci-labels/sheets");
        request.Content = JsonContent.Create(new { ciIds = new[] { first.Id, second.Id, third.Id }, size = "Small" });
        using var response = await _client!.SendAsync(request);
        var content = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("%PDF", Encoding.ASCII.GetString(content, 0, 4));
        Assert.Contains("asset-labels-3", response.Content.Headers.ContentDisposition?.FileName ?? "", StringComparison.Ordinal);
    }

    /// <summary>
    /// The WP's verification step, minus the phone: the QR carries the asset's own URL, and following
    /// it lands on that asset.
    /// </summary>
    [Fact]
    public async Task ScannedLabelUrl_ResolvesToTheCiItWasPrintedFor()
    {
        var ci = await CreateCiAsync("Hardware", "Laptop with a label on its lid");
        var scanned = CiLabelCodes.PayloadFor(BaseUrl, ci.Id);

        Assert.Equal($"{BaseUrl}/assets/{ci.Id}", scanned);
        var found = await LookupAsync(scanned);
        Assert.Equal(ci.Id, found.Id);
        Assert.Equal(ci.Name, found.Name);
    }

    [Fact]
    public async Task Lookup_ResolvesABareIdAnAssetTagAndASerialNumber()
    {
        var assetTag = $"AT-{Guid.NewGuid():N}"[..12];
        var serial = $"SN-{Guid.NewGuid():N}"[..14];
        var ci = await CreateCiAsync("Hardware", "Laptop with both identifiers", assetTag, serial);

        Assert.Equal(ci.Id, (await LookupAsync(ci.Id.ToString())).Id);
        Assert.Equal(ci.Id, (await LookupAsync(assetTag)).Id);
        Assert.Equal(ci.Id, (await LookupAsync(serial)).Id);
        // A wedge scanner has no idea what case the sticker was printed in.
        Assert.Equal(ci.Id, (await LookupAsync(assetTag.ToLowerInvariant())).Id);
        Assert.Equal(ci.Id, (await LookupAsync($"  {serial.ToUpperInvariant()}  ")).Id);
    }

    /// <summary>
    /// Serial first, then asset tag — the same order WP-2.5 gave import dedupe, so a scanner and an
    /// import never disagree about which CI a code names.
    /// </summary>
    [Fact]
    public async Task Lookup_WhenOneCisSerialIsAnothersAssetTag_PrefersTheSerial()
    {
        var shared = $"DUP-{Guid.NewGuid():N}"[..12];
        var byTag = await CreateCiAsync("Hardware", "Laptop wearing the tag", assetTag: shared);
        var bySerial = await CreateCiAsync("Hardware", "Laptop wearing the serial", serialNumber: shared);

        var found = await LookupAsync(shared);

        Assert.Equal(bySerial.Id, found.Id);
        Assert.NotEqual(byTag.Id, found.Id);
    }

    [Fact]
    public async Task Lookup_ForACodeNothingCarries_ReturnsNotFound()
    {
        using var response = await _client!.SendAsync(
            Authenticated(HttpMethod.Get, "/api/cis/lookup?code=NOT-A-REAL-TAG"));
        var problem = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("No asset matches that code", problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Lookup_WithNoCode_ReturnsValidationProblem()
    {
        using var response = await _client!.SendAsync(Authenticated(HttpMethod.Get, "/api/cis/lookup?code=%20"));
        var problem = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Scan or type a code", problem, StringComparison.Ordinal);
    }

    /// <summary>
    /// Failure path: a stale selection must not quietly print a short sheet, because nobody notices
    /// the missing label until the assets are already stickered.
    /// </summary>
    [Fact]
    public async Task LabelSheet_ForAnIdThatNoLongerExists_ReturnsNotFound()
    {
        var ci = await CreateCiAsync("Hardware", "Laptop still on the list");
        var deleted = Guid.CreateVersion7();

        using var request = Authenticated(HttpMethod.Post, "/api/ci-labels/sheets");
        request.Content = JsonContent.Create(new { ciIds = new[] { ci.Id, deleted } });
        using var response = await _client!.SendAsync(request);
        var problem = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains(deleted.ToString(), problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SingleLabel_ForAnUnknownCi_ReturnsNotFound()
    {
        using var response = await _client!.SendAsync(
            Authenticated(HttpMethod.Get, $"/api/cis/{Guid.CreateVersion7()}/label"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task LabelSheet_WithNoCis_ReturnsValidationProblem()
    {
        using var request = Authenticated(HttpMethod.Post, "/api/ci-labels/sheets");
        request.Content = JsonContent.Create(new { ciIds = Array.Empty<Guid>() });
        using var response = await _client!.SendAsync(request);
        var problem = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("at least one configuration item", problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LabelSheet_AboveTheSheetCeiling_ReturnsValidationProblem()
    {
        var tooMany = Enumerable.Range(0, 201).Select(_ => Guid.CreateVersion7()).ToArray();

        using var request = Authenticated(HttpMethod.Post, "/api/ci-labels/sheets");
        request.Content = JsonContent.Create(new { ciIds = tooMany });
        using var response = await _client!.SendAsync(request);
        var problem = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("at most 200 labels", problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Labels_AsEndUser_AreForbidden()
    {
        var ci = await CreateCiAsync("Hardware", "Laptop an end user may not print");

        using var label = await _client!.SendAsync(
            Authenticated(HttpMethod.Get, $"/api/cis/{ci.Id}/label", "EndUser"));
        using var lookup = await _client!.SendAsync(
            Authenticated(HttpMethod.Get, "/api/cis/lookup?code=LT-1", "EndUser"));

        Assert.Equal(HttpStatusCode.Forbidden, label.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, lookup.StatusCode);
    }

    private async Task<CiDto> LookupAsync(string code)
    {
        using var request = Authenticated(HttpMethod.Get, $"/api/cis/lookup?code={Uri.EscapeDataString(code)}");
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Assert.IsType<CiDto>(await response.Content.ReadFromJsonAsync<CiDto>());
    }

    private async Task<CiDto> CreateCiAsync(
        string type,
        string name,
        string? assetTag = null,
        string? serialNumber = null)
    {
        using var request = Authenticated(HttpMethod.Post, "/api/cis");
        request.Content = JsonContent.Create(new
        {
            type,
            name = $"{name} {Guid.NewGuid():N}",
            assetTag,
            serialNumber,
            attributes = AttributesFor(type),
        });
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return Assert.IsType<CiDto>(await response.Content.ReadFromJsonAsync<CiDto>());
    }

    private static Dictionary<string, string> AttributesFor(string type) => type switch
    {
        "Hardware" => new() { ["manufacturer"] = "Dell", ["model"] = "Latitude 5450" },
        "Server" => new()
        {
            ["hostname"] = $"host-{Guid.NewGuid():N}"[..20],
            ["operatingSystem"] = "Ubuntu 24.04",
            ["cpuCores"] = "16",
            ["ramGb"] = "128",
        },
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "No fixture attributes for this CI type."),
    };

    private static HttpRequestMessage Authenticated(HttpMethod method, string uri, string role = "Technician")
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Add(LabelAuthenticationHandler.RoleHeader, role);
        return request;
    }

    private sealed record CiDto(Guid Id, string Type, string Name, string? AssetTag, string? SerialNumber);

    private sealed class LabelApplication : WebApplicationFactory<Program>
    {
        private readonly string _connectionString;
        private readonly string _rabbitMqConnectionString;
        private readonly string _minioConnectionString;

        public LabelApplication(
            string connectionString,
            string rabbitMqConnectionString,
            string minioConnectionString)
        {
            _connectionString = connectionString;
            _rabbitMqConnectionString = rabbitMqConnectionString;
            _minioConnectionString = minioConnectionString;
            Environment.SetEnvironmentVariable("ConnectionStrings__database", connectionString);
            Environment.SetEnvironmentVariable("ConnectionStrings__rabbitmq", rabbitMqConnectionString);
            Environment.SetEnvironmentVariable("Platform__EnableMessageBus", "true");
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Authentication:Authority"] = "https://identity.example.test/realms/it-platform",
                    ["Authentication:Audience"] = "it-platform-api",
                    ["Authentication:ClientId"] = "it-platform-web",
                    ["Authentication:PostLogoutRedirectUri"] = "https://app.example.test/",
                    ["ConnectionStrings:database"] = _connectionString,
                    ["ConnectionStrings:rabbitmq"] = _rabbitMqConnectionString,
                    ["ConnectionStrings:minio"] = _minioConnectionString,
                    ["ObjectStorage:AccessKey"] = "minioadmin",
                    ["ObjectStorage:SecretKey"] = "minio-test-password",
                    ["Platform:ApplyMigrations"] = "false",
                    ["Platform:EnableMessageBus"] = "true",
                    ["Platform:EnableScheduler"] = "false",
                    // A phone cannot reach the host's own loopback, so the printed QR has to carry an
                    // address on the network the scanning device is on.
                    [CiLabelCodes.PublicBaseUrlKey] = BaseUrl,
                }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.AddAuthentication(options =>
                    {
                        options.DefaultAuthenticateScheme = LabelAuthenticationHandler.TestScheme;
                        options.DefaultChallengeScheme = LabelAuthenticationHandler.TestScheme;
                        options.DefaultForbidScheme = LabelAuthenticationHandler.TestScheme;
                    })
                    .AddScheme<AuthenticationSchemeOptions, LabelAuthenticationHandler>(
                        LabelAuthenticationHandler.TestScheme,
                        _ => { });
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            Environment.SetEnvironmentVariable("ConnectionStrings__database", null);
            Environment.SetEnvironmentVariable("ConnectionStrings__rabbitmq", null);
            Environment.SetEnvironmentVariable("Platform__EnableMessageBus", null);
        }
    }

    private sealed class LabelAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string TestScheme = "LabelTest";
        public const string RoleHeader = "X-Test-Role";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(RoleHeader, out var role))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, "label-test-user-id"),
                    new Claim(ClaimTypes.Name, "label-test-user"),
                    new Claim(ClaimTypes.Role, role.ToString()),
                ],
                TestScheme);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), TestScheme)));
        }
    }
}
