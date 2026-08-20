using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

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
using Modules.Assets.Features.DeviceIdentification;
using Platform.Data;

namespace Infrastructure.Tests;

/// <summary>
/// Phase 1 end to end: a catalogue entry is created, a later scan of the same product identifier
/// resolves from it, and everything the parser cannot place is carried through as Unknown rather than
/// guessed at. A deliberately broken provider is registered throughout to prove that a lookup failure
/// never reaches the caller.
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class DeviceIdentificationApiIntegrationTests : IAsyncLifetime
{
    private readonly IdentificationApplication _application;
    private HttpClient? _client;

    public DeviceIdentificationApiIntegrationTests(InfrastructureFixture infrastructure)
    {
        _application = new IdentificationApplication(
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

    /// <summary>The point of the whole feature: the second device of a model identifies itself.</summary>
    [Fact]
    public async Task Identify_ModelIdentifierInTheCatalogue_ResolvesWithHighConfidence()
    {
        var code = UniqueCode();
        await SaveEntryAsync(code, "Lenovo", "ThinkPad X1 Carbon Gen 11", "Laptop");

        var response = await IdentifyAsync($"MTM: {code}");

        Assert.Equal("Lenovo", response.Result.Manufacturer);
        Assert.Equal("ThinkPad X1 Carbon Gen 11", response.Result.Model);
        Assert.Equal("Laptop", response.Result.DeviceType);
        Assert.Equal(IdentificationConfidence.High, response.Result.Confidence);
        Assert.Equal("Manual", response.Result.Source);
    }

    /// <summary>A mapping written once is reusable — that is what makes the catalogue worth having.</summary>
    [Fact]
    public async Task Identify_SameModelIdentifierOnADifferentDevice_ResolvesAgain()
    {
        var code = UniqueCode();
        await SaveEntryAsync(code, "Dell", "Latitude 5450", "Laptop");

        var first = await IdentifyAsync($"P/N: {code}", "S/N: AAAA1111");
        var second = await IdentifyAsync($"P/N: {code}", "S/N: BBBB2222");

        Assert.Equal("Latitude 5450", first.Result.Model);
        Assert.Equal("Latitude 5450", second.Result.Model);
        // The product is shared; the device is not.
        Assert.Equal("AAAA1111", first.Result.SerialNumber);
        Assert.Equal("BBBB2222", second.Result.SerialNumber);
    }

    [Fact]
    public async Task Identify_UnknownIdentifier_ReturnsUnknownRatherThanGuessing()
    {
        var response = await IdentifyAsync($"P/N: {UniqueCode()}");

        Assert.Equal(IdentificationConfidence.Unknown, response.Result.Confidence);
        Assert.Null(response.Result.Manufacturer);
        Assert.Null(response.Result.Model);
    }

    /// <summary>
    /// A serial identifies one machine, so it must never resolve a product. If it did, the catalogue
    /// would be teaching every later device that shares that string what this one happened to be.
    /// </summary>
    [Fact]
    public async Task Identify_SerialMatchingACatalogueKey_DoesNotResolveTheProduct()
    {
        var code = UniqueCode();
        await SaveEntryAsync(code, "Lenovo", "ThinkPad T14", "Laptop");

        var response = await IdentifyAsync($"S/N: {code}");

        Assert.Equal(IdentificationConfidence.Unknown, response.Result.Confidence);
        Assert.Null(response.Result.Model);
        // Preserved as what it is, so the technician still gets it on the form.
        Assert.Equal(code, response.Result.SerialNumber);
    }

    [Fact]
    public async Task Identify_MultipleScans_AreCombinedIntoOneAnswer()
    {
        var code = UniqueCode();
        await SaveEntryAsync(code, "HP", "EliteBook 840 G10", "Laptop");

        var response = await IdentifyAsync($"P/N: {code}", "S/N: 5CD1234ABC");

        Assert.Equal("HP", response.Result.Manufacturer);
        Assert.Equal("5CD1234ABC", response.Result.SerialNumber);
        Assert.Equal(2, response.Identifiers.Count);
    }

    /// <summary>Sweeping the same label twice is normal, not an error and not a second identifier.</summary>
    [Fact]
    public async Task Identify_DuplicateScans_AreCollapsed()
    {
        var response = await IdentifyAsync("S/N: 5CD1234ABC", "S/N: 5CD1234ABC", "SN 5CD1234ABC");

        Assert.Single(response.Identifiers);
        Assert.Equal("5CD1234ABC", response.Identifiers[0].Value);
    }

    /// <summary>One barcode, both facts — and the model half is what the catalogue is keyed on.</summary>
    [Fact]
    public async Task Identify_CombinedLenovoLabel_ResolvesFromItsModelHalf()
    {
        await SaveEntryAsync("12RQ000KUS", "Lenovo", "ThinkPad L14 Gen 2", "Laptop");

        var response = await IdentifyAsync("1S12RQ000KUSMZ00H8S2");

        Assert.Equal("ThinkPad L14 Gen 2", response.Result.Model);
        Assert.Equal("MZ00H8S2", response.Result.SerialNumber);
    }

    [Fact]
    public async Task Identify_OversizedScan_IsReportedAsRejectedRatherThanStored()
    {
        var response = await IdentifyAsync(new string('A', BarcodeParser.MaxLength + 1));

        Assert.Empty(response.Identifiers);
        Assert.Single(response.Rejected);
        Assert.Equal(IdentificationConfidence.Unknown, response.Result.Confidence);
    }

    [Fact]
    public async Task Identify_NoScans_IsAValidationProblem()
    {
        using var request = Authenticated(HttpMethod.Post, "/api/device-identifications");
        request.Content = JsonContent.Create(new { scans = Array.Empty<string>() });

        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    /// <summary>
    /// A provider that throws is registered for every test in this class. That this one passes at all
    /// is the assertion: a broken lookup never reaches the caller and never blocks registration.
    /// </summary>
    [Fact]
    public async Task Identify_WhenAProviderThrows_TheAnswerStillComesBack()
    {
        var code = UniqueCode();
        await SaveEntryAsync(code, "Cisco", "Catalyst 2960X", "Switch");

        var response = await IdentifyAsync($"PID: {code}");

        Assert.Equal("Catalyst 2960X", response.Result.Model);
    }

    [Fact]
    public async Task SaveEntry_TwiceForOneIdentifier_UpdatesRatherThanDuplicating()
    {
        var code = UniqueCode();
        await SaveEntryAsync(code, "Lenovo", "Wrong name", "Laptop");
        await SaveEntryAsync(code, "Lenovo", "ThinkPad X1 Carbon Gen 11", "Laptop");

        await using var scope = _application.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AssetsDbContext>();
        var entries = await dbContext.ProductCatalogEntries
            .Where(entry => entry.ModelIdentifier == code).ToListAsync();

        Assert.Single(entries);
        Assert.Equal("ThinkPad X1 Carbon Gen 11", entries[0].Model);
    }

    /// <summary>A mapping a person typed is usable and unverified, and has to say so.</summary>
    [Fact]
    public async Task SaveEntry_FromATechnician_IsRecordedAsManual()
    {
        var code = UniqueCode();
        var entry = await SaveEntryAsync(code, "Example", "ExampleBook 500", null);

        Assert.Equal("Manual", entry.Source);
        Assert.Equal(code, entry.ModelIdentifier);
    }

    [Fact]
    public async Task Endpoints_RefuseAnEndUser()
    {
        using var request = Authenticated(HttpMethod.Post, "/api/device-identifications", "EndUser");
        request.Content = JsonContent.Create(new { scans = new[] { "S/N: ABC123" } });

        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// The host serialises enums as their names (ConfigureHttpJsonOptions in Web.Host), so a reader
    /// that expects numbers fails on every response carrying a Kind or a Confidence.
    /// </summary>
    private static readonly JsonSerializerOptions ReadOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Version 4, not 7. A version-7 GUID leads with a timestamp, so truncating one gives tests that
    /// run in the same millisecond the same "unique" code — which fails only when the suite is run
    /// together, and passes whenever the failing test is run alone.
    /// </summary>
    private static string UniqueCode() => $"T{Guid.NewGuid():N}"[..16].ToUpperInvariant();

    private async Task<IdentifyResponseDto> IdentifyAsync(params string[] scans)
    {
        using var request = Authenticated(HttpMethod.Post, "/api/device-identifications");
        request.Content = JsonContent.Create(new { scans });
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Assert.IsType<IdentifyResponseDto>(await response.Content.ReadFromJsonAsync<IdentifyResponseDto>(ReadOptions));
    }

    private async Task<CatalogueEntryDto> SaveEntryAsync(
        string modelIdentifier, string manufacturer, string model, string? deviceType)
    {
        using var request = Authenticated(HttpMethod.Post, "/api/product-catalog");
        request.Content = JsonContent.Create(new { modelIdentifier, manufacturer, model, deviceType });
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Assert.IsType<CatalogueEntryDto>(await response.Content.ReadFromJsonAsync<CatalogueEntryDto>(ReadOptions));
    }

    private static HttpRequestMessage Authenticated(HttpMethod method, string uri, string role = "Technician")
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Add(IdentificationAuthenticationHandler.RoleHeader, role);
        return request;
    }

    private sealed record IdentifierDto(string Scanned, string Value, IdentifierKind Kind);

    private sealed record ResultDto(
        string? Manufacturer, string? Model, string? ProductNumber, string? SerialNumber,
        string? DeviceType, string Source, IdentificationConfidence Confidence);

    private sealed record IdentifyResponseDto(
        List<IdentifierDto> Identifiers, List<string> Rejected, ResultDto Result);

    private sealed record CatalogueEntryDto(
        Guid Id, string ModelIdentifier, string Manufacturer, string Model,
        string? ProductNumber, string? DeviceType, string Source);

    /// <summary>Registered for every test here, to prove a failing provider is survivable.</summary>
    private sealed class ThrowingLookupProvider : IDeviceLookupProvider
    {
        // After the local catalogue, so a failure here cannot mask a real answer.
        public int Order => 100;

        public string Name => "Deliberately broken";

        public Task<DeviceIdentificationResult> LookupAsync(
            IReadOnlyList<IdentifierView> identifiers, CancellationToken cancellationToken) =>
            throw new HttpRequestException("The manufacturer API is unreachable.");
    }

    private sealed class IdentificationApplication : WebApplicationFactory<Program>
    {
        private readonly string _connectionString;
        private readonly string _rabbitMqConnectionString;
        private readonly string _minioConnectionString;

        public IdentificationApplication(
            string connectionString, string rabbitMqConnectionString, string minioConnectionString)
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
                }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.AddScoped<IDeviceLookupProvider, ThrowingLookupProvider>();
                services.AddAuthentication(options =>
                    {
                        options.DefaultAuthenticateScheme = IdentificationAuthenticationHandler.TestScheme;
                        options.DefaultChallengeScheme = IdentificationAuthenticationHandler.TestScheme;
                        options.DefaultForbidScheme = IdentificationAuthenticationHandler.TestScheme;
                    })
                    .AddScheme<AuthenticationSchemeOptions, IdentificationAuthenticationHandler>(
                        IdentificationAuthenticationHandler.TestScheme, _ => { });
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

    private sealed class IdentificationAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string TestScheme = "IdentificationTest";
        public const string RoleHeader = "X-Test-Role";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(RoleHeader, out var role))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, "identification-test-user"),
                    new Claim(ClaimTypes.Name, "identification-test-user"),
                    new Claim("preferred_username", "technician1"),
                    new Claim(ClaimTypes.Role, role.ToString()),
                ],
                TestScheme);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), TestScheme)));
        }
    }
}
