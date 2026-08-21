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

using Modules.Assets.Data;
using Modules.Assets.Seeding;
using Modules.Helpdesk.Data;
using Modules.Helpdesk.Seeding;

using Platform.Data;
using Platform.Directory;
using Platform.Seeding;

namespace Infrastructure.Tests;

/// <summary>
/// The seeded estate as an operator meets it: written once, re-runnable, legal against the lifecycle
/// graph, and answering the graph, warranty and ticket-link surfaces through the real endpoints.
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class AssetsInfrastructureSeederIntegrationTests : IAsyncLifetime
{
    private readonly EstateApplication _application;
    private HttpClient? _client;
    private AssetsInfrastructureSeedResult _seeded = new(0, 0, 0, 0, 0, 0, 0, 0);

    public AssetsInfrastructureSeederIntegrationTests(InfrastructureFixture infrastructure)
    {
        _application = new EstateApplication(
            infrastructure.PostgresConnectionString,
            infrastructure.RabbitMqConnectionString,
            infrastructure.MinioConnectionString);
    }

    public async Task InitializeAsync()
    {
        _client = _application.CreateClient();
        await using var scope = _application.Services.CreateAsyncScope();
        var platform = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var helpdesk = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
        var assets = scope.ServiceProvider.GetRequiredService<AssetsDbContext>();
        await platform.Database.MigrateAsync();
        await helpdesk.Database.MigrateAsync();
        await assets.Database.MigrateAsync();

        // The seeders run in the order the console seeder runs them: the estate resolves owners
        // through the platform directory, and the links attach to the seeded backlog.
        await new DemoDataSeeder(platform).SeedAsync();
        await new HelpdeskDemoDataSeeder(helpdesk).SeedAsync();
        await new HelpdeskHistorySeeder(helpdesk).SeedAsync();
        _seeded = await new AssetsInfrastructureSeeder(assets, new DirectoryService(platform)).SeedAsync();
        await new HelpdeskCiLinkSeeder(helpdesk).SeedAsync(new CiLinkPlan(
            _seeded.HardwareCiIds, _seeded.NetworkCiIds, _seeded.ServiceCiIds));
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// The estate's own ids. The fixture database is shared with the other API test classes, so every
    /// assertion about "the estate" has to say which rows it means.
    /// </summary>
    private HashSet<Guid> SeededCiIds => [.. _seeded.CiIds.Values];

    [Fact]
    public async Task SeedAsync_RunTwice_WritesTheEstateOnce()
    {
        await using var scope = _application.Services.CreateAsyncScope();
        var assets = scope.ServiceProvider.GetRequiredService<AssetsDbContext>();
        var platform = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

        var second = await new AssetsInfrastructureSeeder(assets, new DirectoryService(platform)).SeedAsync();

        Assert.Equal(0, second.CisAdded);
        Assert.Equal(0, second.RelationshipsAdded);
        Assert.Equal(0, second.VendorsAdded);
        Assert.Equal(0, second.ContractsAdded);
        Assert.Equal(0, second.CustomFieldsAdded);
        Assert.Equal(0, second.CustomFieldValuesAdded);
        Assert.Equal(0, second.LifecycleEntriesAdded);
        Assert.Equal(0, second.AssignmentEntriesAdded);

        // Scoped to the seeded rows: this fixture's database is shared with the other API test classes,
        // which create configuration items, vendors and contracts of their own.
        var seededIds = SeededCiIds;
        var vendorNames = AssetsEstate.Vendors.Select(vendor => vendor.Name).ToArray();
        var poNumbers = AssetsEstate.Contracts.Select(contract => contract.Number).ToArray();
        Assert.Equal(AssetsInfrastructureSeeder.CiCount, await assets.Cis.CountAsync(ci => seededIds.Contains(ci.Id)));
        Assert.Equal(
            AssetsEstate.Relationships.Count,
            await assets.CiRelationships.CountAsync(relationship => seededIds.Contains(relationship.SourceCiId)));
        Assert.Equal(vendorNames.Length, await assets.Vendors.CountAsync(vendor => vendorNames.Contains(vendor.Name)));
        Assert.Equal(
            poNumbers.Length,
            await assets.Contracts.CountAsync(contract => poNumbers.Contains(contract.PoNumber)));
        // The ids are handed to the helpdesk seeder on every run, whether or not rows were added.
        Assert.NotEmpty(second.NetworkCiIds);
        Assert.Equal(_seeded.NetworkCiIds, second.NetworkCiIds);
    }

    /// <summary>
    /// WP-4.4's fixture rides on this estate: its installs name the seeded laptops by key. Re-running
    /// must add nothing, and the compliance case the WP verifies has to be there afterwards.
    /// </summary>
    /// <summary>
    /// WP-5.8's demo fixture: a draft change on the switch the Phase 3 rig can stop, so approving one and
    /// watching the alerts go quiet needs nothing typed. It stays a draft — approving it is the act being
    /// demonstrated — and running the seeder again adds no second copy.
    /// </summary>
    [Fact]
    public async Task SeedAsync_ChangeRequest_SeedsOneDraftOnTheDownableSwitchAndOnlyOnce()
    {
        await using var scope = _application.Services.CreateAsyncScope();
        var assets = scope.ServiceProvider.GetRequiredService<AssetsDbContext>();

        var first = await new ChangeRequestSeeder(assets).SeedAsync(_seeded.CiIds);
        var second = await new ChangeRequestSeeder(assets).SeedAsync(_seeded.CiIds);

        Assert.Equal(0, second.ChangesAdded);
        Assert.NotNull(first.ChangeId);
        Assert.Equal(first.ChangeId, second.ChangeId);

        var change = await assets.ChangeRequests
            .Include(item => item.Cis)
            .SingleAsync(item => item.Id == first.ChangeId);
        Assert.Equal(ChangeRequestStatus.Draft, change.Status);
        Assert.Equal(_seeded.CiIds[ChangeRequestSeeder.CiKey], Assert.Single(change.Cis).CiId);

        // Approvable whenever somebody gets to it: the workflow refuses a slot that has already ended,
        // and a fixed calendar date would silently drift into the past (WP-2.8).
        Assert.True(change.PlannedEndAt > DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task SeedAsync_SoftwareInventory_IsWrittenOnceAndLandsOnTheSeededLaptops()
    {
        await using var scope = _application.Services.CreateAsyncScope();
        var assets = scope.ServiceProvider.GetRequiredService<AssetsDbContext>();

        await new SoftwareCatalogSeeder(assets).SeedAsync(_seeded.CiIds);
        var second = await new SoftwareCatalogSeeder(assets).SeedAsync(_seeded.CiIds);

        Assert.Equal(0, second.ProductsAdded);
        Assert.Equal(0, second.RulesAdded);
        Assert.Equal(0, second.InstallsAdded);
        Assert.Equal(0, second.LicensePoolsAdded);

        var seededIds = SeededCiIds;
        var installs = await assets.InstalledSoftware
            .Where(install => seededIds.Contains(install.CiId)).ToListAsync();
        Assert.Equal(SoftwareCatalogSeeder.Installs.Count, installs.Count);
        Assert.Equal(5, installs.Select(install => install.CiId).Distinct().Count());

        // The over-deployment the WP verifies: three seats, five machines.
        var acrobat = await assets.SoftwareProducts.SingleAsync(product => product.Name == "Acrobat Pro");
        var pool = await assets.LicensePools.SingleAsync(item => item.ProductId == acrobat.Id);
        Assert.Equal(3, pool.Entitlements);
        Assert.Equal(5, installs.Count(install => install.ProductId == acrobat.Id));

        // And the one raw name nothing claims, which is what the re-normalise demo is performed on.
        Assert.Contains(installs, install => install.ProductId is null && install.RawName == "Contoso VPN Client");
    }

    /// <summary>
    /// The seeder writes history rows directly instead of walking <c>ICiLifecycleService</c>, so the
    /// guard it bypassed is asserted here against the transition table the migration seeded.
    /// </summary>
    [Fact]
    public async Task SeedAsync_LifecycleHistory_OnlyUsesTransitionsTheGraphPermits()
    {
        await using var scope = _application.Services.CreateAsyncScope();
        var assets = scope.ServiceProvider.GetRequiredService<AssetsDbContext>();

        var seededIds = SeededCiIds;
        var legal = (await assets.CiLifecycleTransitions.ToListAsync())
            .Select(transition => (transition.FromState, transition.ToState))
            .ToHashSet();
        var history = await assets.CiLifecycleHistory
            .Where(entry => seededIds.Contains(entry.CiId)).ToListAsync();

        Assert.NotEmpty(history);
        Assert.All(history, entry => Assert.Contains((entry.FromState, entry.ToState), legal));
        // Every CI's final state must be where its history ends, or the record contradicts itself.
        var cis = await assets.Cis.Where(ci => seededIds.Contains(ci.Id))
            .Select(ci => new { ci.Id, ci.LifecycleState }).ToListAsync();
        foreach (var ci in cis)
        {
            var last = history.Where(entry => entry.CiId == ci.Id)
                .OrderBy(entry => entry.OccurredAt).LastOrDefault();
            Assert.Equal(ci.LifecycleState, last?.ToState ?? CiLifecycleState.Ordered);
        }
    }

    /// <summary>
    /// The WP's verification line: the graph endpoints return multi-level trees. Each site's router is
    /// the root of its own tree, and asking what an outage there takes with it walks the whole site.
    /// </summary>
    [Theory]
    [InlineData("DC1 core router", 5, 30)]
    [InlineData("HQ edge router", 3, 10)]
    [InlineData("Branch router", 5, 6)]
    public async Task ImpactedBy_ASiteRouter_ReturnsAMultiLevelTree(string rootName, int expectedDepth, int minimumNodes)
    {
        var root = await FindCiAsync(rootName);

        var impact = await GetAsync<GraphDto>($"/api/cis/{root.Id}/impacted-by?maxDepth=10");

        Assert.False(impact.ContainsCycle);
        Assert.False(impact.MaxDepthReached);
        // impacted-by includes the root itself at depth 0 (WP-2.3).
        Assert.Contains(impact.Nodes, node => node.Id == root.Id && node.Depth == 0);
        Assert.Equal(expectedDepth, impact.Nodes.Max(node => node.Depth));
        Assert.True(impact.Nodes.Count >= minimumNodes,
            $"{rootName} reached {impact.Nodes.Count} CIs, expected at least {minimumNodes}");
        Assert.Equal(impact.Nodes.Count, impact.Nodes.Select(node => node.Id).Distinct().Count());
    }

    /// <summary>
    /// The other direction: a business service walks down through its software and virtual machines to
    /// the physical host, the switch and finally the router it ultimately needs.
    /// </summary>
    [Fact]
    public async Task Ancestors_ABusinessService_WalksDownToTheHardwareItNeeds()
    {
        var service = await FindCiAsync("Finance Reporting Service");

        var ancestors = await GetAsync<GraphDto>($"/api/cis/{service.Id}/ancestors?maxDepth=10");

        Assert.DoesNotContain(ancestors.Nodes, node => node.Id == service.Id);
        var names = ancestors.Nodes.Select(node => node.Name).ToArray();
        Assert.Contains("Finance ERP", names);
        Assert.Contains("Finance database server", names);
        Assert.Contains("DC1 hypervisor host 2", names);
        Assert.Contains("DC1 core switch A", names);
        Assert.Contains("DC1 core router", names);
        Assert.Equal(
            ["NetworkDevice", "Server", "Software", "Virtual"],
            ancestors.Nodes.Select(node => node.Type).Distinct().Order());
    }

    [Fact]
    public async Task ListCis_FilteredByWarrantyWindow_ReturnsOnlyAssetsInsideIt()
    {
        var expiring = await GetAsync<CiPageDto>("/api/cis?warrantyExpiringWithinDays=30&pageSize=200");
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        Assert.NotEmpty(expiring.Items);
        Assert.All(expiring.Items, ci =>
        {
            Assert.NotNull(ci.Coverage.WarrantyExpiresAt);
            Assert.True(ci.Coverage.WarrantyExpiresAt!.Value.DayNumber - today.DayNumber <= 30);
        });

        Assert.Contains(expiring.Items, ci => SeededCiIds.Contains(ci.Id));

        // The estate itself must show every warranty state, or the screens cannot be checked by eye.
        await using var scope = _application.Services.CreateAsyncScope();
        var assets = scope.ServiceProvider.GetRequiredService<AssetsDbContext>();
        var seededIds = SeededCiIds;
        var estate = await assets.Cis.Where(ci => seededIds.Contains(ci.Id)).ToListAsync();

        var warranties = estate.Select(ci => ci.WarrantyExpiresAt).OfType<DateOnly>().ToArray();
        Assert.Contains(warranties, date => date.DayNumber - today.DayNumber > 30);
        Assert.Contains(warranties, date => date.DayNumber - today.DayNumber is >= 0 and <= 30);
        Assert.Contains(warranties, date => date.DayNumber < today.DayNumber);
        Assert.Contains(estate, ci => ci.ContractId is not null);
        Assert.Contains(estate, ci => ci.OwnerUserId is not null);
        Assert.Contains(estate, ci => ci.OwnerUserId is null && ci.SiteId is not null);
    }

    /// <summary>
    /// WP-3.7 asks an alert on a seeded switch to show an owner and a location. The three network CIs
    /// the monitoring seeder takes (<c>NetworkCiIds.Take(3)</c>, which is estate order) are therefore
    /// the ones that have to carry the whole context — a demo where every field reads "none recorded"
    /// proves the enrichment is wired but says nothing about whether it is right.
    /// </summary>
    [Fact]
    public async Task SeedAsync_TheMonitoredNetworkCis_CarryTheContextAnAlertIsSupposedToShow()
    {
        await using var scope = _application.Services.CreateAsyncScope();
        var assets = scope.ServiceProvider.GetRequiredService<AssetsDbContext>();
        // The fourth is WP-3.12's down-able device — the one the Phase 3 demo stops, so the one whose
        // ticket somebody actually reads. It was unowned until this package's hand-verification put
        // "Owner: nobody holds this asset" on the demo's own screen.
        var monitored = _seeded.CiIds
            .Where(entry => entry.Key is "dc1-core-rtr-01" or "dc1-core-sw-01" or "dc1-core-sw-02"
                or "dc1-acc-sw-01")
            .Select(entry => entry.Value)
            .ToList();
        Assert.Equal(4, monitored.Count);

        var cis = await assets.Cis.Where(ci => monitored.Contains(ci.Id)).ToListAsync();

        Assert.All(cis, ci =>
        {
            Assert.Equal("Technician Two", ci.OwnerName);
            Assert.Equal("Primary Data Centre", ci.SiteName);
            Assert.NotNull(ci.WarrantyExpiresAt);
            Assert.NotNull(ci.ContractId);
        });
    }

    [Fact]
    public async Task SeedAsync_TicketLinks_AttachSeededTicketsToTheEstate()
    {
        await using var scope = _application.Services.CreateAsyncScope();
        var helpdesk = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
        var assets = scope.ServiceProvider.GetRequiredService<AssetsDbContext>();

        var seededIds = SeededCiIds;
        var links = await helpdesk.TicketCiLinks.Where(link => seededIds.Contains(link.CiId)).ToListAsync();
        Assert.NotEmpty(links);
        Assert.All(links, link => Assert.Equal("seeder", link.LinkedById));

        // Every link must resolve to a CI that is really there: nothing enforces it, because a foreign
        // key cannot span the helpdesk and assets schemas.
        var ciIds = await assets.Cis.Select(ci => ci.Id).ToHashSetAsync();
        Assert.All(links, link => Assert.Contains(link.CiId, ciIds));

        var second = await new HelpdeskCiLinkSeeder(helpdesk).SeedAsync(new CiLinkPlan(
            _seeded.HardwareCiIds, _seeded.NetworkCiIds, _seeded.ServiceCiIds));
        Assert.Equal(0, second.LinksAdded);
        Assert.Equal(links.Count, await helpdesk.TicketCiLinks.CountAsync(link => seededIds.Contains(link.CiId)));
    }

    /// <summary>
    /// Failure path: the estate's own edges protect it. A switch half the site depends on cannot be
    /// deleted, and the refusal names what is in the way rather than cascading the graph away.
    /// </summary>
    [Fact]
    public async Task DeleteCi_ThatTheEstateStillDependsOn_ReturnsConflict()
    {
        var switchCi = await FindCiAsync("DC1 core switch A");

        using var request = Authenticated(HttpMethod.Delete, $"/api/cis/{switchCi.Id}");
        using var response = await _client!.SendAsync(request);
        var problem = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("relationship", problem, StringComparison.OrdinalIgnoreCase);

        await using var scope = _application.Services.CreateAsyncScope();
        var assets = scope.ServiceProvider.GetRequiredService<AssetsDbContext>();
        Assert.True(await assets.Cis.AnyAsync(ci => ci.Id == switchCi.Id));
    }

    /// <summary>
    /// Failure path: the estate names people, departments and sites that only the platform demo seeder
    /// creates. Without them the run must stop before writing anything rather than write half an estate.
    /// </summary>
    [Fact]
    public async Task SeedAsync_WithoutThePlatformDirectory_FailsBeforeWritingAnything()
    {
        await using var scope = _application.Services.CreateAsyncScope();
        var assets = scope.ServiceProvider.GetRequiredService<AssetsDbContext>();
        var before = await assets.Cis.CountAsync();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new AssetsInfrastructureSeeder(assets, new EmptyDirectoryService()).SeedAsync());

        Assert.Contains("platform directory does not hold", exception.Message, StringComparison.Ordinal);
        Assert.Contains("manager1", exception.Message, StringComparison.Ordinal);
        Assert.Equal(before, await assets.Cis.CountAsync());
    }

    private async Task<CiDto> FindCiAsync(string name)
    {
        var page = await GetAsync<CiPageDto>($"/api/cis?search={Uri.EscapeDataString(name)}&pageSize=200");
        return Assert.Single(page.Items, ci => ci.Name == name);
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
        request.Headers.Add(EstateAuthenticationHandler.RoleHeader, role);
        return request;
    }

    /// <summary>A directory that holds nobody, for the "seeded before the platform data" failure path.</summary>
    private sealed class EmptyDirectoryService : IDirectoryService
    {
        public Task<IReadOnlyList<DirectoryUser>> ListUsersAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DirectoryUser>>([]);

        public Task<DirectoryUser?> FindUserAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult<DirectoryUser?>(null);

        public Task<IReadOnlyList<DirectoryDepartment>> ListDepartmentsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DirectoryDepartment>>([]);

        public Task<DirectoryDepartment?> FindDepartmentAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult<DirectoryDepartment?>(null);

        public Task<IReadOnlyList<DirectorySite>> ListSitesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DirectorySite>>([]);

        public Task<DirectorySite?> FindSiteAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult<DirectorySite?>(null);
    }

    private sealed record CiCoverageDto(
        Guid? ContractId,
        string? ContractName,
        string? PoNumber,
        string? VendorName,
        DateOnly? ContractEndDate,
        DateOnly? PurchaseDate,
        DateOnly? WarrantyExpiresAt);

    private sealed record CiOwnershipDto(
        Guid? OwnerUserId,
        string? OwnerName,
        Guid? DepartmentId,
        string? DepartmentName,
        Guid? SiteId,
        string? SiteName,
        DateTimeOffset? AssignedAt);

    private sealed record CiDto(
        Guid Id,
        string Type,
        string Name,
        string? AssetTag,
        string? SerialNumber,
        string LifecycleState,
        CiOwnershipDto Ownership,
        CiCoverageDto Coverage);

    private sealed record CiPageDto(List<CiDto> Items, int Total, int Page, int PageSize);

    private sealed record GraphNodeDto(Guid Id, string Type, string Name, string LifecycleState, int Depth);

    private sealed record GraphDto(
        Guid RootCiId,
        string Direction,
        int MaxDepth,
        bool MaxDepthReached,
        bool ContainsCycle,
        List<GraphNodeDto> Nodes);

    private sealed class EstateApplication : WebApplicationFactory<Program>
    {
        private readonly string _connectionString;
        private readonly string _rabbitMqConnectionString;
        private readonly string _minioConnectionString;

        public EstateApplication(
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
                }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.AddAuthentication(options =>
                    {
                        options.DefaultAuthenticateScheme = EstateAuthenticationHandler.TestScheme;
                        options.DefaultChallengeScheme = EstateAuthenticationHandler.TestScheme;
                        options.DefaultForbidScheme = EstateAuthenticationHandler.TestScheme;
                    })
                    .AddScheme<AuthenticationSchemeOptions, EstateAuthenticationHandler>(
                        EstateAuthenticationHandler.TestScheme,
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

    private sealed class EstateAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string TestScheme = "AssetsEstateTest";
        public const string RoleHeader = "X-Test-Role";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(RoleHeader, out var role))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, "assets-estate-test-user-id"),
                    new Claim(ClaimTypes.Name, "assets-estate-test-user"),
                    new Claim(ClaimTypes.Role, role.ToString()),
                ],
                TestScheme);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), TestScheme)));
        }
    }
}
