using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;

using Contracts.Events;

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
using Modules.Assets.Features.Discovery;
using Modules.Helpdesk.Data;
using Modules.Monitoring.Data;
using Platform.Data;

namespace Infrastructure.Tests;

/// <summary>
/// The drift report end to end: a CI somebody typed, an observation the real WP-4.2 intake filed
/// against it, and the disagreement between them coming back off the API.
/// <para>
/// The observed half is driven through <c>IDiscoveryReviewService.IngestAsync</c> rather than by
/// writing <c>ci_discovery_facts</c> by hand, so what this asserts is that a real sighting reaches the
/// report — and, just as importantly, that the intake still leaves the CI's own attributes alone.
/// </para>
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class DriftReportApiIntegrationTests : IAsyncLifetime
{
    private readonly DriftApplication _application;
    private HttpClient? _client;

    public DriftReportApiIntegrationTests(InfrastructureFixture infrastructure)
    {
        ArgumentNullException.ThrowIfNull(infrastructure);
        _application = new DriftApplication(
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

        // Both ports a CI touches. The discovery intake's top rung reads Monitoring through
        // IMonitoredAddressDirectory and deleting a CI asks Helpdesk through ITicketLinkDirectory; an
        // unmigrated schema behind either answers 500 from a query that mentions neither this feature
        // nor that module. The seventh package to meet this trap.
        await scope.ServiceProvider.GetRequiredService<MonitoringDbContext>().Database.MigrateAsync();
        await scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>().Database.MigrateAsync();
    }

    public Task DisposeAsync()
    {
        _application.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>
    /// The WP's own verification step: the recorded location and the reported one disagree, and the
    /// report says so — while the CI keeps saying exactly what the operator typed.
    /// </summary>
    [Fact]
    public async Task Drift_WhenTheRecordedSiteDisagreesWithTheReportedLocation_FlagsItAndLeavesTheCiAlone()
    {
        var address = NewAddress();
        var ci = await CreateNetworkCiAsync(address, "Head Office");

        await IngestAsync(address, $"sw-{Guid.NewGuid():N}"[..12], sysLocation: "Primary Data Centre");

        var drift = await GetDriftAsync(ci);
        var item = Assert.Single(drift.Items, entry => entry.CiId == ci.Id);
        var finding = Assert.Single(item.Findings, entry => entry.Field == "location");

        Assert.Equal("Changed", finding.Kind);
        Assert.Equal("Head Office", finding.RecordedValue);
        Assert.Equal("Primary Data Centre", finding.ObservedValue);

        // A scan observes; an operator asserts. If a sighting ever started writing the CI's own
        // attributes, this report would have two copies of one number and nothing to say.
        await using var scope = _application.Services.CreateAsyncScope();
        var stored = await scope.ServiceProvider.GetRequiredService<AssetsDbContext>()
            .Cis.AsNoTracking().FirstAsync(entity => entity.Id == ci.Id);
        Assert.Equal("Head Office", stored.SiteName);
    }

    [Fact]
    public async Task Drift_WhenTheDeviceAnswersOnAnotherAddress_FlagsTheManagementIp()
    {
        var recorded = NewAddress();
        var found = NewAddress();
        var ci = await CreateNetworkCiAsync(recorded);

        // Matched by name rather than by address, which is exactly the case a re-addressed device is:
        // the CMDB's management IP is no longer where the thing answers.
        await IngestAsync(found, ci.Name, sysLocation: null);

        var drift = await GetDriftAsync(ci);
        var item = Assert.Single(drift.Items, entry => entry.CiId == ci.Id);
        var finding = Assert.Single(item.Findings, entry => entry.Field == "managementIp");

        Assert.Equal("Changed", finding.Kind);
        Assert.Equal(recorded, finding.RecordedValue);
        Assert.Equal(found, finding.ObservedValue);
    }

    /// <summary>
    /// A CI nothing has scanned is not drift. Silence about a laptop in a drawer is the normal state of
    /// an estate, and reporting it would bury the switch that genuinely moved.
    /// </summary>
    [Fact]
    public async Task Drift_ForACiNoScanHasEverReported_SaysNothingAboutIt()
    {
        var ci = await CreateNetworkCiAsync(NewAddress());

        var drift = await GetDriftAsync(ci);

        Assert.DoesNotContain(drift.Items, entry => entry.CiId == ci.Id);
    }

    /// <summary>
    /// The filter narrows a CI's findings rather than choosing between CIs, so asking for changed
    /// fields never returns a row whose findings are all of another kind.
    /// </summary>
    [Fact]
    public async Task Drift_FilteredToOneKind_KeepsOnlyThatKindOfFinding()
    {
        var address = NewAddress();
        var ci = await CreateNetworkCiAsync(address, "Head Office");
        await IngestAsync(address, $"sw-{Guid.NewGuid():N}"[..12], sysLocation: "Primary Data Centre");

        var changed = await GetDriftAsync(ci, "&kind=Changed");
        var missing = await GetDriftAsync(ci, "&kind=Missing&field=location");

        Assert.All(changed.Items, item => Assert.All(item.Findings, finding => Assert.Equal("Changed", finding.Kind)));
        Assert.Contains(changed.Items, item => item.CiId == ci.Id);
        Assert.DoesNotContain(missing.Items, item => item.CiId == ci.Id);
    }

    /// <summary>
    /// A device that answered SNMP and left sysLocation empty is making a statement; one that only
    /// answered a ping is silent. The gate between the two is what keeps the report readable.
    /// </summary>
    [Fact]
    public async Task Drift_ForADeviceThatAnsweredNothingButAPing_ReportsNoMissingFields()
    {
        var address = NewAddress();
        var ci = await CreateNetworkCiAsync(address, "Head Office");

        await IngestAsync(address, sysName: null, sysLocation: null);

        var drift = await GetDriftAsync(ci);
        var item = drift.Items.FirstOrDefault(entry => entry.CiId == ci.Id);

        Assert.DoesNotContain(item?.Findings ?? [], finding => finding.Field == "location");
    }

    /// <summary>
    /// The cable somebody patched and nobody wrote down — the finding WP-4.3 deliberately declined to
    /// write into <c>ci_relationships</c> so that this report would have something to find.
    /// </summary>
    [Fact]
    public async Task Drift_WhenAScanReportsANeighbourNoRelationshipRecords_ListsItAsAnUnrecordedLink()
    {
        var reporterAddress = NewAddress();
        var reporter = await CreateNetworkCiAsync(reporterAddress);
        var neighbour = await CreateNetworkCiAsync(NewAddress());

        await IngestAsync(reporterAddress, reporter.Name, sysLocation: null,
            neighbours: [new DiscoveredNeighbour("lldp", "GigabitEthernet0/2", neighbour.Name, "GigabitEthernet0/1", null)]);

        var drift = await GetDriftAsync(reporter);

        var link = Assert.Single(drift.UnrecordedLinks, entry =>
            new[] { entry.SourceCiId, entry.TargetCiId }.ToHashSet().SetEquals([reporter.Id, neighbour.Id]));
        Assert.Contains("lldp", link.Protocols);
        Assert.False(link.ConfirmedByBothEnds);
    }

    /// <summary>An observed cable a relationship already describes is agreement, not a finding.</summary>
    [Fact]
    public async Task Drift_WhenTheNeighbourMatchesARecordedRelationship_LeavesItOffTheUnrecordedList()
    {
        var reporterAddress = NewAddress();
        var reporter = await CreateNetworkCiAsync(reporterAddress);
        var neighbour = await CreateNetworkCiAsync(NewAddress());

        using var relate = Authenticated(HttpMethod.Post, $"/api/cis/{reporter.Id}/relationships");
        relate.Content = JsonContent.Create(new { targetCiId = neighbour.Id, type = "ConnectsTo" });
        using var related = await _client!.SendAsync(relate);
        Assert.Equal(HttpStatusCode.Created, related.StatusCode);

        await IngestAsync(reporterAddress, reporter.Name, sysLocation: null,
            neighbours: [new DiscoveredNeighbour("lldp", "GigabitEthernet0/2", neighbour.Name, "GigabitEthernet0/1", null)]);

        var drift = await GetDriftAsync(reporter);

        Assert.DoesNotContain(drift.UnrecordedLinks, entry =>
            new[] { entry.SourceCiId, entry.TargetCiId }.ToHashSet().SetEquals([reporter.Id, neighbour.Id]));
    }

    /// <summary>Failure path: a kind that is not a finding kind is a 400 naming the three that are.</summary>
    [Fact]
    public async Task Drift_WithAFindingKindThatDoesNotExist_ReturnsValidationProblem()
    {
        using var request = Authenticated(HttpMethod.Get, "/api/drift?kind=Suspicious");
        using var response = await _client!.SendAsync(request);
        var problem = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("is not a drift finding kind", problem, StringComparison.Ordinal);
    }

    /// <summary>
    /// Failure path: zero days would mean everything is stale, which reads as a broken report rather
    /// than a filter nobody meant to set.
    /// </summary>
    [Fact]
    public async Task Drift_WithAStalenessThresholdOfZero_ReturnsValidationProblem()
    {
        using var request = Authenticated(HttpMethod.Get, "/api/drift?staleAfterDays=0");
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("staleAfterDays", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Drift_AsEndUser_IsForbidden()
    {
        using var request = Authenticated(HttpMethod.Get, "/api/drift", "EndUser");
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// The staleness threshold is a request parameter precisely so an operator arguing about whether a
    /// device is really gone can move it without a deployment.
    /// </summary>
    [Fact]
    public async Task Drift_WithATighterStalenessThreshold_ReportsADeviceSeenLongerAgoThanThat()
    {
        var address = NewAddress();
        var ci = await CreateNetworkCiAsync(address);
        await IngestAsync(address, ci.Name, sysLocation: null);
        await BackdateSightingAsync(ci.Id, TimeSpan.FromDays(3));

        var lenient = await GetDriftAsync(ci, "&field=lastSeen");
        var strict = await GetDriftAsync(ci, "&field=lastSeen&staleAfterDays=1");

        Assert.DoesNotContain(lenient.Items, item => item.CiId == ci.Id);
        var item = Assert.Single(strict.Items, entry => entry.CiId == ci.Id);
        Assert.Equal("Missing", Assert.Single(item.Findings).Kind);
    }

    private static string NewAddress() =>
        $"10.{Random.Shared.Next(100, 250)}.{Random.Shared.Next(1, 250)}.{Random.Shared.Next(1, 250)}";

    /// <summary>
    /// A CI of this class's own, pinned to a site nothing else uses.
    /// <para>
    /// The site is the isolation. The report is estate-wide over a database the whole suite shares, so
    /// a read with no scope competes for its page with every CI every other class has ever created —
    /// the shared-table trap in the shape WP-4.3 met with the topology map's node budget. Every read
    /// here is scoped to the CI's own site, which no other test writes to.
    /// </para>
    /// </summary>
    private async Task<CiDto> CreateNetworkCiAsync(string managementIp, string? siteName = null)
    {
        using var request = Authenticated(HttpMethod.Post, "/api/cis");
        request.Content = JsonContent.Create(new
        {
            type = "NetworkDevice",
            name = $"drift-sw-{Guid.NewGuid():N}"[..24],
            attributes = new Dictionary<string, string>
            {
                ["managementIp"] = managementIp,
                ["vendor"] = "Cisco",
                ["portCount"] = "24",
            },
        });
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var ci = Assert.IsType<CiDto>(await response.Content.ReadFromJsonAsync<CiDto>());
        return ci with { SiteId = await SetSiteAsync(ci.Id, siteName) };
    }

    /// <summary>
    /// Written through the DbContext rather than the assignment endpoint: a site is a Platform
    /// directory row this suite's database may or may not have been seeded with, and what is under test
    /// is the comparison rather than how the name got onto the CI.
    /// </summary>
    private async Task<Guid> SetSiteAsync(Guid ciId, string? siteName)
    {
        await using var scope = _application.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AssetsDbContext>();
        var ci = await dbContext.Cis.FirstAsync(entity => entity.Id == ciId);
        var siteId = Guid.CreateVersion7();
        ci.SiteId = siteId;
        ci.SiteName = siteName;
        await dbContext.SaveChangesAsync();
        return siteId;
    }

    private Task<DriftDto> GetDriftAsync(CiDto ci, string query = "") =>
        GetAsync<DriftDto>($"/api/drift?siteId={ci.SiteId}&pageSize=200{query}");

    private async Task BackdateSightingAsync(Guid ciId, TimeSpan age)
    {
        await using var scope = _application.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AssetsDbContext>();
        var facts = await dbContext.CiDiscoveryFacts.FirstAsync(entity => entity.CiId == ciId);
        facts.LastSeenAt = DateTimeOffset.UtcNow - age;
        await dbContext.SaveChangesAsync();
    }

    /// <summary>One scan sighting, through the real WP-4.2 intake.</summary>
    private async Task IngestAsync(
        string address,
        string? sysName,
        string? sysLocation,
        DiscoveredNeighbour[]? neighbours = null)
    {
        await using var scope = _application.Services.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<IDiscoveryReviewService>()
            .IngestAsync(
                new DeviceDiscovered(
                    Guid.CreateVersion7(),
                    DateTimeOffset.UtcNow,
                    "drift-test-scanner",
                    Guid.CreateVersion7(),
                    "Drift test profile",
                    Guid.CreateVersion7(),
                    address,
                    null,
                    null,
                    RespondedToPing: true,
                    [],
                    sysName is null
                        ? null
                        : new DiscoveredSnmpIdentity(sysName, "Simulated switch", "1.3.6.1.4.1.8072.3.2.10",
                            sysLocation, null, 1),
                    neighbours ?? []),
                CancellationToken.None);

        Assert.Equal(DiscoveredDeviceStatus.Matched, result.Status);
    }

    private async Task<T> GetAsync<T>(string uri)
    {
        using var request = Authenticated(HttpMethod.Get, uri);
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Assert.IsType<T>(await response.Content.ReadFromJsonAsync<T>());
    }

    private static HttpRequestMessage Authenticated(HttpMethod method, string uri, string role = "Technician")
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Add(DriftAuthenticationHandler.RoleHeader, role);
        return request;
    }

    private sealed record CiDto(Guid Id, string Type, string Name)
    {
        /// <summary>Filled in after creation; the API's own response carries ownership, not this shape.</summary>
        public Guid SiteId { get; init; }
    }

    private sealed record FindingDto(string Field, string Label, string Kind, string? RecordedValue, string? ObservedValue);

    private sealed record CiDriftDto(
        Guid CiId,
        string Name,
        string Type,
        string? SiteName,
        string Address,
        DateTimeOffset LastSeenAt,
        List<FindingDto> Findings);

    private sealed record UnrecordedLinkDto(
        Guid SourceCiId,
        string SourceCiName,
        string? SourcePort,
        Guid TargetCiId,
        string TargetCiName,
        string? TargetPort,
        List<string> Protocols,
        bool ConfirmedByBothEnds);

    private sealed record SummaryDto(
        int CisObserved,
        int CisWithDrift,
        int Changed,
        int New,
        int Missing,
        int UnrecordedLinks,
        int UnmatchedDiscoveries,
        int StaleAfterDays,
        DateTimeOffset GeneratedAt);

    private sealed record DriftDto(
        SummaryDto Summary,
        List<CiDriftDto> Items,
        List<UnrecordedLinkDto> UnrecordedLinks,
        int Total,
        int Page,
        int PageSize);

    private sealed class DriftApplication : WebApplicationFactory<Program>
    {
        private readonly string _connectionString;
        private readonly string _rabbitMqConnectionString;
        private readonly string _minioConnectionString;

        public DriftApplication(
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
                    // Creating a CI publishes through the outbox, so the bus has to be configured even
                    // though nothing here reads a message. Every hosted service is removed below, so no
                    // sweeper of this host's competes with another suite's (WP-3.12's trap).
                    ["Platform:EnableMessageBus"] = "true",
                    ["Platform:EnableScheduler"] = "false",
                    // The estate-wide read has to see this class's own CIs rather than compete for a
                    // rendering budget with every CI the suite has ever created.
                    ["Assets:Topology:NodeLimit"] = "100000",
                }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.AddAuthentication(options =>
                    {
                        options.DefaultAuthenticateScheme = DriftAuthenticationHandler.TestScheme;
                        options.DefaultChallengeScheme = DriftAuthenticationHandler.TestScheme;
                        options.DefaultForbidScheme = DriftAuthenticationHandler.TestScheme;
                    })
                    .AddScheme<AuthenticationSchemeOptions, DriftAuthenticationHandler>(
                        DriftAuthenticationHandler.TestScheme,
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

    private sealed class DriftAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string TestScheme = "DriftTest";
        public const string RoleHeader = "X-Test-Role";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(RoleHeader, out var role))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, "drift-test-user-id"),
                    new Claim(ClaimTypes.Name, "drift-test-user"),
                    new Claim(ClaimTypes.Role, role.ToString()),
                ],
                TestScheme);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), TestScheme)));
        }
    }
}
