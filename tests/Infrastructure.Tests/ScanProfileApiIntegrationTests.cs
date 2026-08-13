using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
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
using Modules.Monitoring.Data;
using Platform.Data;

namespace Infrastructure.Tests;

/// <summary>
/// The WP's configuration half: scan profiles created and edited through the operator API, and read
/// back by the discovery service through its own.
/// <para>
/// Two audiences and two policies, which is the thing most worth guarding here. The operator surface
/// is <c>CanManageMonitoring</c>; the scanner's fetch is <c>CanDiscover</c>, which is the
/// <c>Discovery</c> realm role and nothing else — not Admin, and deliberately not <c>Poller</c>. That
/// separation is what keeps a stolen scanner token away from everything the credential vault protects.
/// </para>
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class ScanProfileApiIntegrationTests : IAsyncLifetime
{
    private const string DiscoveryRole = "Discovery";
    private const string PollerRole = "Poller";

    private readonly ScanProfileApplication _application;
    private HttpClient? _client;

    public ScanProfileApiIntegrationTests(InfrastructureFixture infrastructure)
    {
        _application = new ScanProfileApplication(
            infrastructure.PostgresConnectionString,
            infrastructure.RabbitMqConnectionString,
            infrastructure.MinioConnectionString);
    }

    public async Task InitializeAsync()
    {
        _client = _application.CreateClient();
        await using var scope = _application.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<PlatformDbContext>().Database.MigrateAsync();
        await scope.ServiceProvider.GetRequiredService<MonitoringDbContext>().Database.MigrateAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task ScanProfile_Created_IsReadBackWithItsRangesPortsAndAddressCount()
    {
        var group = NewGroup();

        var created = await CreateAsync(NewName(), group, ["10.30.0.0/29", "192.0.2.1"], [22, 443]);

        Assert.Equal(group, created.DiscoveryGroup);
        Assert.Equal(["10.30.0.0/29", "192.0.2.1"], created.Ranges);
        Assert.Equal([22, 443], created.Ports);
        // Six hosts out of the /29 plus the single address. The figure exists so an operator can see
        // that a /16 they typed is 65,534 probes before the scanner tries them.
        Assert.Equal(7, created.AddressCount);
        Assert.True(created.SnmpEnabled);
        Assert.True(created.NeighbourDiscoveryEnabled);

        var fetched = await GetAsync<ScanProfileDto>($"/api/scan-profiles/{created.Id}");
        Assert.Equal(created.Id, fetched.Id);
    }

    /// <summary>
    /// The discovery service's own read, and the WP's "scan profiles configured via API" half joined
    /// up: what an operator creates is what the scanner is handed.
    /// </summary>
    [Fact]
    public async Task DiscoveryConfig_ReturnsTheEnabledProfilesOfItsOwnGroup()
    {
        var group = NewGroup();
        var other = NewGroup();
        var mine = await CreateAsync(NewName(), group, ["10.31.0.0/29"], [22]);
        await CreateAsync(NewName(), other, ["10.32.0.0/29"], []);
        await CreateAsync(NewName(), group, ["10.33.0.0/29"], [], isEnabled: false);

        var config = await GetAsync<DiscoveryConfigDto>(
            $"/api/discovery/{group}/scan-profiles", DiscoveryRole);

        Assert.Equal(group, config.DiscoveryGroup);
        var only = Assert.Single(config.Profiles);
        Assert.Equal(mine.Id, only.ScanProfileId);
        Assert.Equal(["10.31.0.0/29"], only.Ranges);
        Assert.Equal([22], only.Ports);
        // Seconds on the wire, minutes in the model: the scanner schedules against a monotonic clock
        // in seconds, so the conversion happens once, here.
        Assert.Equal(60 * 60, only.IntervalSeconds);
    }

    /// <summary>
    /// A group nobody has written a profile for is an empty list, not a 404. A scanner is deployed
    /// before anybody configures it, and answering 404 would make "nothing to scan yet" and "this
    /// platform has never heard of you" the same message on its first cycle.
    /// </summary>
    [Fact]
    public async Task DiscoveryConfig_ForAGroupWithNoProfiles_IsEmptyRatherThanNotFound()
    {
        var config = await GetAsync<DiscoveryConfigDto>(
            $"/api/discovery/{NewGroup()}/scan-profiles", DiscoveryRole);

        Assert.Empty(config.Profiles);
    }

    [Fact]
    public async Task ScanProfile_Disabled_LeavesTheScannersConfiguration()
    {
        var group = NewGroup();
        var created = await CreateAsync(NewName(), group, ["10.34.0.0/29"], []);
        Assert.Single((await GetAsync<DiscoveryConfigDto>(
            $"/api/discovery/{group}/scan-profiles", DiscoveryRole)).Profiles);

        using var edit = Authenticated(HttpMethod.Put, $"/api/scan-profiles/{created.Id}");
        edit.Content = JsonContent.Create(new
        {
            name = created.Name,
            ranges = new[] { "10.34.0.0/29" },
            discoveryGroup = group,
            intervalMinutes = 60,
            timeoutSeconds = 2,
            isEnabled = false,
        });
        using var edited = await _client!.SendAsync(edit);
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);

        var after = await GetAsync<DiscoveryConfigDto>(
            $"/api/discovery/{group}/scan-profiles", DiscoveryRole);
        Assert.Empty(after.Profiles);
    }

    /// <summary>
    /// An update is a complete statement, like every other one in this module: an omitted port list
    /// clears the fingerprint step rather than leaving the previous one quietly in place.
    /// </summary>
    [Fact]
    public async Task ScanProfile_UpdatedWithoutPorts_ClearsThemRatherThanKeepingTheOldOnes()
    {
        var group = NewGroup();
        var created = await CreateAsync(NewName(), group, ["10.35.0.0/29"], [22, 443]);

        using var edit = Authenticated(HttpMethod.Put, $"/api/scan-profiles/{created.Id}");
        edit.Content = JsonContent.Create(new
        {
            name = created.Name,
            ranges = new[] { "10.35.0.0/29" },
            discoveryGroup = group,
            intervalMinutes = 60,
            timeoutSeconds = 2,
            isEnabled = true,
        });
        using var edited = await _client!.SendAsync(edit);
        var updated = Assert.IsType<ScanProfileDto>(
            await edited.Content.ReadFromJsonAsync<ScanProfileDto>());

        Assert.Empty(updated.Ports);
    }

    [Fact]
    public async Task ScanProfile_TheLocalKeyword_IsStoredAndHasNoAddressCount()
    {
        var created = await CreateAsync(NewName(), NewGroup(), ["local"], []);

        Assert.Equal(["local"], created.Ranges);
        // Its size depends on the interface the scanner finds, so a number here would be a guess
        // presented as a fact.
        Assert.Null(created.AddressCount);
    }

    [Fact]
    public async Task ScanProfile_Deleted_IsGoneFromTheListAndTheScannersConfiguration()
    {
        var group = NewGroup();
        var created = await CreateAsync(NewName(), group, ["10.36.0.0/29"], []);

        using var delete = Authenticated(HttpMethod.Delete, $"/api/scan-profiles/{created.Id}");
        using var deleted = await _client!.SendAsync(delete);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        using var refetch = Authenticated(HttpMethod.Get, $"/api/scan-profiles/{created.Id}");
        using var response = await _client.SendAsync(refetch);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty((await GetAsync<DiscoveryConfigDto>(
            $"/api/discovery/{group}/scan-profiles", DiscoveryRole)).Profiles);
    }

    /// <summary>Every write endpoint produces an audit entry — ARCHITECTURE §7.1.</summary>
    [Fact]
    public async Task ScanProfile_EveryWrite_IsAudited()
    {
        var created = await CreateAsync(NewName(), NewGroup(), ["10.37.0.0/29"], []);

        using var delete = Authenticated(HttpMethod.Delete, $"/api/scan-profiles/{created.Id}");
        using var deleted = await _client!.SendAsync(delete);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        await using var scope = _application.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var actions = await dbContext.AuditEntries.AsNoTracking()
            .Where(entry => entry.EntityType == "ScanProfile" && entry.EntityId == created.Id.ToString())
            .Select(entry => entry.Action)
            .ToListAsync();

        Assert.Equal(["Created", "Deleted"], actions.Order());
    }

    [Fact]
    public async Task ScanProfile_ARangeThatCannotBeScanned_IsRefusedWithAFieldError()
    {
        using var request = Authenticated(HttpMethod.Post, "/api/scan-profiles");
        request.Content = JsonContent.Create(new
        {
            name = NewName(),
            ranges = new[] { "10.0.0.0/8" },
            discoveryGroup = NewGroup(),
            intervalMinutes = 60,
            timeoutSeconds = 2,
        });

        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadAsStringAsync();
        Assert.Contains("Ranges", problem, StringComparison.Ordinal);
        // Both numbers, so an operator can see what to type instead.
        Assert.Contains("65,536", problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScanProfile_WithNoRanges_IsRefused()
    {
        using var request = Authenticated(HttpMethod.Post, "/api/scan-profiles");
        request.Content = JsonContent.Create(new
        {
            name = NewName(),
            ranges = Array.Empty<string>(),
            discoveryGroup = NewGroup(),
            intervalMinutes = 60,
            timeoutSeconds = 2,
        });

        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ScanProfile_ANameAlreadyUsed_IsAConflict()
    {
        var name = NewName();
        await CreateAsync(name, NewGroup(), ["10.38.0.0/29"], []);

        using var request = Authenticated(HttpMethod.Post, "/api/scan-profiles");
        request.Content = JsonContent.Create(new
        {
            name,
            ranges = new[] { "10.39.0.0/29" },
            discoveryGroup = NewGroup(),
            intervalMinutes = 60,
            timeoutSeconds = 2,
        });

        using var response = await _client!.SendAsync(request);

        // Two profiles with one name are indistinguishable in the log line that says which scan found
        // a device, which is the only place most people will ever read a profile's name.
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    /// <summary>
    /// The failure path that matters most: <c>CanDiscover</c> is disjoint from every operator policy
    /// <em>and</em> from <c>CanPoll</c>. A scanner has no devices to configure and no credential scope
    /// to redeem, so the two service identities must not be interchangeable.
    /// </summary>
    [Theory]
    [InlineData("Technician")]
    [InlineData("Admin")]
    [InlineData(PollerRole)]
    public async Task DiscoveryConfig_WithAnyRoleButDiscovery_IsForbidden(string role)
    {
        using var request = Authenticated(
            HttpMethod.Get, $"/api/discovery/{NewGroup()}/scan-profiles", role);

        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>And the reverse: a scanner cannot write itself a wider range to scan.</summary>
    [Theory]
    [InlineData(DiscoveryRole)]
    [InlineData("EndUser")]
    public async Task ScanProfiles_ManagedWithoutAnOperatorRole_IsForbidden(string role)
    {
        using var list = Authenticated(HttpMethod.Get, "/api/scan-profiles", role);
        using var listed = await _client!.SendAsync(list);
        Assert.Equal(HttpStatusCode.Forbidden, listed.StatusCode);

        using var create = Authenticated(HttpMethod.Post, "/api/scan-profiles", role);
        create.Content = JsonContent.Create(new
        {
            name = NewName(),
            ranges = new[] { "10.40.0.0/29" },
            intervalMinutes = 60,
            timeoutSeconds = 2,
        });
        using var created = await _client.SendAsync(create);
        Assert.Equal(HttpStatusCode.Forbidden, created.StatusCode);
    }

    [Fact]
    public async Task ScanProfile_ThatDoesNotExist_IsNotFound()
    {
        using var request = Authenticated(
            HttpMethod.Get, $"/api/scan-profiles/{Guid.CreateVersion7()}");

        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static string NewGroup() => $"group-{Guid.NewGuid():N}"[..20];

    private static string NewName() => $"Scan {Guid.NewGuid():N}"[..20];

    private async Task<ScanProfileDto> CreateAsync(
        string name,
        string discoveryGroup,
        string[] ranges,
        int[] ports,
        bool isEnabled = true)
    {
        using var request = Authenticated(HttpMethod.Post, "/api/scan-profiles");
        request.Content = JsonContent.Create(new
        {
            name,
            ranges,
            discoveryGroup,
            ports,
            intervalMinutes = 60,
            timeoutSeconds = 2,
            isEnabled,
        });
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return Assert.IsType<ScanProfileDto>(await response.Content.ReadFromJsonAsync<ScanProfileDto>());
    }

    private async Task<T> GetAsync<T>(string uri, string role = "Technician")
    {
        using var request = Authenticated(HttpMethod.Get, uri, role);
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Assert.IsType<T>(await response.Content.ReadFromJsonAsync<T>());
    }

    private static HttpRequestMessage Authenticated(
        HttpMethod method,
        string uri,
        string role = "Technician")
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Add(ScanProfileAuthenticationHandler.RoleHeader, role);
        return request;
    }

    private sealed record ScanProfileDto(
        Guid Id,
        string Name,
        string? Description,
        string DiscoveryGroup,
        IReadOnlyList<string> Ranges,
        IReadOnlyList<int> Ports,
        int IntervalMinutes,
        int TimeoutSeconds,
        bool SnmpEnabled,
        bool NeighbourDiscoveryEnabled,
        bool IsEnabled,
        long? AddressCount);

    private sealed record DiscoveryConfigDto(
        string DiscoveryGroup,
        IReadOnlyList<DiscoveryScanProfileDto> Profiles,
        DateTimeOffset GeneratedAt);

    private sealed record DiscoveryScanProfileDto(
        Guid ScanProfileId,
        string Name,
        IReadOnlyList<string> Ranges,
        IReadOnlyList<int> Ports,
        int IntervalSeconds,
        int TimeoutSeconds,
        bool SnmpEnabled,
        bool NeighbourDiscoveryEnabled);

    /// <summary>
    /// Its own host rather than a shared one, following every other API test class here. A scan profile
    /// needs no CI, so this host does not migrate the assets schema — but it does migrate Platform's,
    /// because every write is audited.
    /// </summary>
    private sealed class ScanProfileApplication : WebApplicationFactory<Program>
    {
        private readonly string _connectionString;
        private readonly string _rabbitMqConnectionString;
        private readonly string _minioConnectionString;

        public ScanProfileApplication(
            string connectionString,
            string rabbitMqConnectionString,
            string minioConnectionString)
        {
            _connectionString = connectionString;
            _rabbitMqConnectionString = rabbitMqConnectionString;
            _minioConnectionString = minioConnectionString;
            Environment.SetEnvironmentVariable("ConnectionStrings__database", connectionString);
            Environment.SetEnvironmentVariable("ConnectionStrings__rabbitmq", rabbitMqConnectionString);
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
                    ["Platform:EnableMessageBus"] = "false",
                    ["Platform:EnableScheduler"] = "false",
                }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.AddAuthentication(options =>
                    {
                        options.DefaultAuthenticateScheme = ScanProfileAuthenticationHandler.TestScheme;
                        options.DefaultChallengeScheme = ScanProfileAuthenticationHandler.TestScheme;
                        options.DefaultForbidScheme = ScanProfileAuthenticationHandler.TestScheme;
                    })
                    .AddScheme<AuthenticationSchemeOptions, ScanProfileAuthenticationHandler>(
                        ScanProfileAuthenticationHandler.TestScheme,
                        _ => { });
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            Environment.SetEnvironmentVariable("ConnectionStrings__database", null);
            Environment.SetEnvironmentVariable("ConnectionStrings__rabbitmq", null);
        }
    }

    private sealed class ScanProfileAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string TestScheme = "ScanProfileTest";
        public const string RoleHeader = "X-Test-Role";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(RoleHeader, out var role))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, "discovery-test-user-id"),
                    new Claim(ClaimTypes.Name, "discovery-test-user"),
                    new Claim(ClaimTypes.Role, role.ToString()),
                ],
                TestScheme);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), TestScheme)));
        }
    }
}
